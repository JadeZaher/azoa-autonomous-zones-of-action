using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Deployments;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Xunit;
using ActionGraphBundle = AZOA.WebAPI.Models.ActionGraphBundle;
using User = AZOA.WebAPI.Models.User;
using Wallet = AZOA.WebAPI.Models.Wallet;

namespace AZOA.WebAPI.Tests.Deployments;

/// <summary>Audit-1 H1 + ADV F-H12: native deployment signing is bound to the caller's credential and scope; parked rows have an operator exit.</summary>
public sealed class DeploymentDirectSigningAuthorityTests
{
    public static TheoryData<string> RejectedCredentials => new() { "none", "scoped-key-without-deploy-scope", "child-of-another-tenant" };

    [Theory]
    [MemberData(nameof(RejectedCredentials))]
    public async Task Native_start_without_matching_credential_authority_never_prepares_or_signs(string credentialCase)
    {
        var harness = await Harness.Create();
        var credential = credentialCase switch
        {
            "none" => null,
            "scoped-key-without-deploy-scope" => harness.ApiKey([AzoaScopes.DappManage]),
            _ => harness.Child(Guid.NewGuid(), [AzoaScopes.DappManage, AzoaScopes.ContractDeploySign]),
        };

        var result = await harness.StartAsync(credential);

        result.IsError.Should().BeTrue();
        harness.Module.Verify(module => module.PrepareNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Module.Verify(module => module.SubmitNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<NativeStagePreparation>(),
            It.IsAny<CancellationToken>()), Times.Never);
        harness.Store.Operations.Should().OnlyContain(row => !row.SubmissionAuthorized && row.PreparedJson == null);
    }

    [Fact]
    public async Task Owner_credential_binds_the_signer_and_fences_both_preparation_and_first_submission()
    {
        var harness = await Harness.Create();

        var result = await harness.StartAsync(harness.Owner());

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.State.Should().Be("Submitted");
        harness.Store.FencedSigning.Should().HaveCount(2, "the Preparing CAS and the SubmissionAuthorized CAS each lock the authority");
        harness.Store.FencedSigning.SelectMany(authorities => authorities).Should().OnlyContain(authority =>
            authority.Wallet.Id == harness.WalletId && authority.RequiredScopes.SequenceEqual(new[] { AzoaScopes.ContractDeploySign }));
        harness.Module.Verify(module => module.SubmitNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<NativeStagePreparation>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Authority_revoked_inside_the_transaction_refuses_before_any_custody()
    {
        var harness = await Harness.Create();
        harness.Store.SigningRevoked = true;

        var result = await harness.StartAsync(harness.Owner());

        result.IsError.Should().BeTrue();
        harness.Module.Verify(module => module.PrepareNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Operator_closes_a_parked_operation_only_while_its_observation_stays_unresolvable()
    {
        var harness = await Harness.Create();
        var parked = await harness.ParkAsync();

        var closed = await harness.Service.ResolveNeedsOperatorAsync(parked.Id, Guid.NewGuid(),
            new DeploymentOperatorResolution(parked.Version, "CloseUnresolvable", "RPC history pruned; tx unknown", AcknowledgeNoFurtherEffect: true));

        closed.IsError.Should().BeFalse(closed.Message);
        closed.Result!.State.Should().Be("Failed");
        (await harness.Store.ListPendingAsync(10)).Should().BeEmpty("a closed row is no longer re-observed by the worker");
        (await harness.Service.ListNeedsOperatorAsync(10)).Should().BeEmpty();
        var resumed = await harness.Service.ContinueAsync(parked.Id, harness.Actor, null, default, harness.Owner());
        resumed.Result!.State.Should().Be("Failed", "a terminal row can never authorize a new stage");
    }

    [Fact]
    public async Task Operator_close_defers_to_a_definitive_chain_answer()
    {
        var harness = await Harness.Create();
        var parked = await harness.ParkAsync();
        harness.Observation = AZOAResult<NativeStageObservation>.Success(new("Pending", "tx-1", "protocol"));

        var resolved = await harness.Service.ResolveNeedsOperatorAsync(parked.Id, Guid.NewGuid(),
            new DeploymentOperatorResolution(parked.Version, "CloseUnresolvable", "stuck", AcknowledgeNoFurtherEffect: true));

        resolved.IsError.Should().BeFalse(resolved.Message);
        resolved.Result!.State.Should().Be("Submitted", "the saved wire is still live, so the operator may not close it");
    }

    private sealed class Harness
    {
        public InMemoryDeploymentStore Store { get; } = new();
        public Mock<INativeOperationModule> Module { get; } = new();
        public DeploymentService Service { get; private set; } = null!;
        public Guid Actor { get; } = Guid.NewGuid();
        public Guid WalletId { get; } = Guid.NewGuid();
        public AZOAResult<NativeStageObservation> Observation { get; set; } = AZOAResult<NativeStageObservation>.Success(new("Pending", "tx-1", "protocol"));
        private DeploymentTarget Target { get; } = new("NativeContract", "Ethereum", "Devnet");
        private NativeArtifact Native { get; } = new("evm-contract/1", JsonSerializer.SerializeToElement(new EvmContractArtifact("0x6000", "0x6000")), "fixture");

        public static async Task<Harness> Create()
        {
            var harness = new Harness();
            var bundleId = Guid.NewGuid();
            var bundles = new Mock<IActionGraphBundleStore>();
            bundles.Setup(store => store.GetByIdAsync(bundleId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<IActionGraphBundle>.Success(new ActionGraphBundle { Id = bundleId, UserId = harness.Actor, IsActive = true }));
            var provider = new Mock<IBlockchainProvider>();
            var factory = new Mock<IBlockchainProviderFactory>();
            factory.Setup(item => item.GetProvider("Ethereum", ChainNetwork.Devnet)).Returns(provider.Object);
            factory.Setup(item => item.TryGetModule<INativeOperationModule>(provider.Object, out It.Ref<INativeOperationModule?>.IsAny))
                .Returns((IBlockchainProvider _, out INativeOperationModule? module) => { module = harness.Module.Object; return true; });
            harness.Module.Setup(module => module.QuoteNativeOperationAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<NativeOperationQuote>.Success(new("", "", "protocol", "100", "0", "0", "{}", DateTimeOffset.UtcNow.AddMinutes(10))));
            harness.Module.Setup(module => module.GetNextStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<IReadOnlyList<NativeStageHistory>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<NativeNextStage>.Success(new("deploy", false, Reservation: new("100", "0", "0"))));
            harness.Module.Setup(module => module.PrepareNativeStageAsync(It.IsAny<NativeOperationIntent>(), "deploy", It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<NativeStagePreparation>.Success(new("deploy", "tx-1", "AA==", "protocol", "{}", "100", "0")));
            harness.Module.Setup(module => module.ObserveNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<NativeStagePreparation>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Observation);
            harness.Module.Setup(module => module.SubmitNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<NativeStagePreparation>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<string>.Success("tx-1"));
            var access = new Mock<IValueAccessService>();
            access.Setup(service => service.RequireValueAccessAsync(harness.Actor, It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<bool>.Success(true));
            var governance = new Mock<INodeGovernanceGuard>();
            governance.Setup(guard => guard.EnsureChainAllowedAsync("Ethereum", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(NodeGovernanceDecision.Allowed);
            var wallets = new Mock<IWalletStore>();
            wallets.Setup(store => store.GetByIdAsync(harness.WalletId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<IWallet>.Success(new Wallet { Id = harness.WalletId, UserId = harness.Actor, ChainType = "Ethereum",
                    Address = "0x1111111111111111111111111111111111111111", WalletType = WalletType.Platform, EncryptedPrivateKey = "sealed" }));
            var consent = new Mock<ITenantConsentGate>();
            consent.Setup(gate => gate.EnsureAllowedAsync(It.IsAny<SigningContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<bool>.Success(true));
            var users = new Mock<IUserStore>();
            users.Setup(store => store.GetByIdAsync(harness.Actor, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<IUser>.Success(new User { Id = harness.Actor, IsActive = true }));
            var authorities = new SigningAdmissionAuthorityFactory(users.Object, wallets.Object, Mock.Of<ISurrealExecutor>(MockBehavior.Strict));
            harness.Service = new DeploymentService(harness.Store, bundles.Object, Mock.Of<IActionStore>(), Mock.Of<IHolonManager>(),
                new DeploymentArtifactCompiler(Mock.Of<IActionDagValidator>()), factory.Object, access.Object, governance.Object,
                Mock.Of<IFrozenActionRuntime>(), wallets.Object, consent.Object, Mock.Of<IEcosystemStore>(), Mock.Of<IDappSeriesStore>(),
                Mock.Of<IDeploymentRecoveryAuditStore>(MockBehavior.Strict), authorities);
            var payload = new DeploymentArtifactPayload(2, bundleId, harness.Actor, null, harness.Target, null, harness.Native);
            await harness.Store.AddArtifactAsync(new DeploymentArtifact { Id = "artifact", ArtifactHash = "artifact-hash", PayloadHash = "payload-hash",
                OwnerUserId = harness.Actor.ToString("N"), TenantId = "", BundleId = bundleId.ToString("N"),
                PayloadJson = JsonSerializer.Serialize(payload), CreatedAt = DateTimeOffset.UtcNow });
            return harness;
        }

        public async Task<AZOAResult<DeploymentReceipt>> StartAsync(SigningCredentialWitness? credential)
        {
            var intent = new NativeOperationIntent(1, "", "Deploy", "Ethereum", "Devnet", "", "artifact-hash",
                JsonSerializer.SerializeToElement(new EvmNativeInput(Native, "0x1111111111111111111111111111111111111111")),
                [new("0x1111111111111111111111111111111111111111", new SigningContext(Actor, WalletId, false, GrantorUserId: Actor, Scope: AzoaScopes.ContractDeploySign))],
                new("1000", "0", "0", ""));
            var quoted = await Service.QuoteAsync(Actor, null, new DeploymentQuoteRequest("artifact", "request-1", intent));
            quoted.IsError.Should().BeFalse(quoted.Message);
            return await Service.StartWithCredentialAsync(Actor, null, "request-1",
                new DeploymentStartRequest("artifact", "artifact-hash", Target, quoted.Result), credential);
        }

        public async Task<DeploymentOperation> ParkAsync()
        {
            (await StartAsync(Owner())).IsError.Should().BeFalse();
            Observation = AZOAResult<NativeStageObservation>.Failure("RPC history unavailable");
            var row = Store.Operations.Single();
            await Service.ObserveAsync(row.Id);
            var parked = Store.Operations.Single();
            parked.State.Should().Be("NeedsOperator");
            return parked;
        }

        public SigningCredentialWitness Owner() => new(1, "Jwt", Actor, null, null, DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(10), null, null, [], true, TokenUse: AzoaClaims.TokenUseLogin);

        public SigningCredentialWitness ApiKey(IReadOnlyList<string> scopes) => new(1, "ApiKey", Actor, null, null, null,
            null, Guid.NewGuid(), "hash", scopes, false, scopes);

        public SigningCredentialWitness Child(Guid tenant, IReadOnlyList<string> scopes) => new(1, "Jwt", Actor, tenant, null,
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10), null, null, scopes, false, TokenUse: AzoaClaims.TokenUseChild);
    }
}
