// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Kyc;

/// <summary>
/// The tenant custodial projection must agree with the value gate about AML. A row
/// that reads Approved + Ready while <c>KycGateService</c> denies is how a sponsor
/// caught by a sanctions-name collision is stranded with nothing to point support at.
/// </summary>
public sealed class TenantCustodialScreeningProjectionTests
{
    private const string ExternalSubject = "user-42";

    private readonly Mock<ITenantManager> _tenants = new();
    private readonly Mock<IWalletManager> _wallets = new();
    private readonly Mock<IKycManager> _kyc = new();
    private readonly Mock<IBlockchainProviderFactory> _providers = new();
    private readonly Mock<IIdempotencyStore> _idempotency = new();
    private readonly InMemoryKycScreeningStore _screening = new();

    [Fact]
    public async Task ApprovedIdentityWithACleanScreening_IsReadyAndCarriesNoReviewDate()
    {
        var tenantId = Guid.NewGuid();
        var userId = SeedApprovedSubject(tenantId);
        _screening.SeedResult(userId, tenantId, KycScreeningDecision.NO_HIT);

        var status = await CreateManager().GetStatusAsync(tenantId, ExternalSubject);

        status.IsError.Should().BeFalse();
        status.Result!.KycStatus.Should().Be(TenantKycStatus.Approved);
        status.Result.Ready.Should().BeTrue();
        status.Result.ReviewDueAt.Should().BeNull();
        status.Result.UnavailableReason.Should().BeNull();
    }

    [Theory]
    [InlineData(KycScreeningDecision.MANUAL_REVIEW)]
    [InlineData(KycScreeningDecision.HIT)]
    public async Task ApprovedIdentityWithAnAdverseScreening_ProjectsUnderReviewAndIsNotReady(
        KycScreeningDecision decision)
    {
        var tenantId = Guid.NewGuid();
        var userId = SeedApprovedSubject(tenantId);
        var screenedAt = DateTimeOffset.UtcNow.AddHours(-2);
        _screening.SeedResult(userId, tenantId, decision, screenedAt);

        var status = await CreateManager().GetStatusAsync(tenantId, ExternalSubject);

        status.IsError.Should().BeFalse();
        status.Result!.KycStatus.Should().Be(TenantKycStatus.UnderReview);
        status.Result.Ready.Should().BeFalse(
            "the value gate denies this subject, so the projection must not say ready");
        status.Result.ReviewDueAt.Should().Be(
            screenedAt.AddHours(KycAmlClearance.DefaultManualReviewSlaHours));
        status.Result.UnavailableReason.Should().Be(KycAmlClearance.UnderReviewReason);
    }

    [Fact]
    public async Task UnderReviewProjection_DisclosesNoAmlDetail()
    {
        var tenantId = Guid.NewGuid();
        var userId = SeedApprovedSubject(tenantId);
        _screening.SeedResult(userId, tenantId, KycScreeningDecision.HIT);

        var status = await CreateManager().GetStatusAsync(tenantId, ExternalSubject);

        var reason = status.Result!.UnavailableReason!;
        reason.Should().NotContainEquivalentOf("sanction");
        reason.Should().NotContainEquivalentOf("aml");
        reason.Should().NotContainEquivalentOf("watchlist");
        reason.Should().NotContainEquivalentOf("identomat");
        reason.Should().NotContainEquivalentOf("score");
    }

    [Fact]
    public async Task ApprovedIdentityWithATruncatedCleanScreening_StillProjectsUnderReview()
    {
        // A saturated hit list is an unfinished question. NO_HIT over an incomplete
        // list can never be read as a pass — the same rule KycGateService applies.
        var tenantId = Guid.NewGuid();
        var userId = SeedApprovedSubject(tenantId);
        _screening.SeedResult(
            userId, tenantId, KycScreeningDecision.NO_HIT, resultsTruncated: true);

        var status = await CreateManager().GetStatusAsync(tenantId, ExternalSubject);

        status.Result!.KycStatus.Should().Be(TenantKycStatus.UnderReview);
        status.Result.Ready.Should().BeFalse();
    }

    [Fact]
    public async Task ApprovedIdentityWithNoScreeningRow_ProjectsUnderReviewNotReady()
    {
        // Never screened is not "clean". The gate denies it, so the projection must.
        var tenantId = Guid.NewGuid();
        SeedApprovedSubject(tenantId);

        var status = await CreateManager().GetStatusAsync(tenantId, ExternalSubject);

        status.Result!.KycStatus.Should().Be(TenantKycStatus.UnderReview);
        status.Result.Ready.Should().BeFalse();
        status.Result.ReviewDueAt.Should().BeNull("the SLA clock starts at the screening");
    }

    [Fact]
    public async Task ScreeningStoreOutage_ReadsAsUnavailableNotAsNotReady()
    {
        // W1.1's rule: an outage must not read to a sponsor as "you did not finish
        // setup". It must also never read as cleared.
        var tenantId = Guid.NewGuid();
        SeedApprovedSubject(tenantId);
        _screening.FailReads = true;

        var status = await CreateManager().GetStatusAsync(tenantId, ExternalSubject);

        status.IsError.Should().BeTrue();
        status.Message.Should().StartWith("TENANT_KYC_UNAVAILABLE");
    }

    [Fact]
    public async Task AnotherTenantsCleanScreening_DoesNotClearThisTenantsSubject()
    {
        // The screening row is scoped to (user, tenant). A cross-tenant read would
        // let one tenant's clearance open another tenant's gate.
        var tenantId = Guid.NewGuid();
        var userId = SeedApprovedSubject(tenantId);
        _screening.SeedResult(userId, Guid.NewGuid(), KycScreeningDecision.NO_HIT);

        var status = await CreateManager().GetStatusAsync(tenantId, ExternalSubject);

        status.Result!.KycStatus.Should().Be(TenantKycStatus.UnderReview);
    }

    [Fact]
    public async Task PendingIdentity_NeverConsultsScreeningAndKeepsItsOwnReason()
    {
        var tenantId = Guid.NewGuid();
        var userId = SeedSubject(tenantId, KycStatus.PENDING);
        _screening.SeedResult(userId, tenantId, KycScreeningDecision.HIT);

        var status = await CreateManager().GetStatusAsync(tenantId, ExternalSubject);

        status.Result!.KycStatus.Should().Be(TenantKycStatus.Pending);
        status.Result.UnavailableReason.Should().Be("KYC verification is not approved.");
    }

    [Fact]
    public async Task ApprovedIdentityWithNoScreeningStoreRegistered_FailsClosedAsUnavailable()
    {
        // The optional dependency must never mean "assume clear". Losing the store
        // is a node misconfiguration, not a clearance.
        var tenantId = Guid.NewGuid();
        SeedApprovedSubject(tenantId);

        var status = await CreateManager(registerScreeningStore: false).GetStatusAsync(tenantId, ExternalSubject);

        status.IsError.Should().BeTrue();
        status.Message.Should().StartWith("TENANT_KYC_UNAVAILABLE");
    }

    [Fact]
    public async Task ManualReviewSla_IsOperatorConfigurableAndClamped()
    {
        var tenantId = Guid.NewGuid();
        var userId = SeedApprovedSubject(tenantId);
        var screenedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        _screening.SeedResult(userId, tenantId, KycScreeningDecision.MANUAL_REVIEW, screenedAt);

        var status = await CreateManager(manualReviewSlaHours: "0")
            .GetStatusAsync(tenantId, ExternalSubject);

        status.Result!.ReviewDueAt.Should().Be(
            screenedAt.AddHours(KycAmlClearance.MinimumManualReviewSlaHours),
            "a zero or negative SLA would remove the deadline entirely");
    }

    // ── Custody mode reaches the capability projection (W1.8 adoption) ──────

    [Fact]
    public void Capabilities_RecogniseAProductionCustodyModeOutsideDevelopment()
    {
        var manager = CreateManager(
            custodyMode: CustodyModeResolver.PlatformMode,
            blockchainMode: "Live",
            environmentName: "Production");

        var capabilities = manager.GetCapabilities();

        capabilities.CustodyAvailable.Should().BeTrue(
            "the node must recognise a production custody mode or walletReady is false by construction");
        capabilities.WalletProvisioningReady.Should().BeTrue();
        capabilities.CustodyMode.Should().Be(CustodyModeResolver.PlatformMode);
    }

    [Fact]
    public void Capabilities_ProductionCustodyOnASimulatedChainStaysUnavailable()
    {
        var manager = CreateManager(
            custodyMode: CustodyModeResolver.PlatformMode,
            blockchainMode: "Simulated",
            environmentName: "Production");

        var capabilities = manager.GetCapabilities();

        capabilities.CustodyAvailable.Should().BeFalse();
        capabilities.UnavailableReason.Should().Be(
            CustodyModeResolver.PlatformCustodyInSimulatedModeReason);
    }

    [Fact]
    public void Capabilities_NeverEchoAnUnrecognisedCustodyModeBackToATenant()
    {
        var manager = CreateManager(
            custodyMode: "TrustMeBro",
            blockchainMode: "Live",
            environmentName: "Production");

        var capabilities = manager.GetCapabilities();

        capabilities.CustodyAvailable.Should().BeFalse();
        capabilities.UnavailableReason.Should().NotContain("TrustMeBro");
        capabilities.UnavailableReason.Should().Contain("key management");
    }

    [Fact]
    public void Capabilities_NeverNameTheWalletEncryptionKeyConfigurationPathToATenant()
    {
        var manager = CreateManager(
            custodyMode: CustodyModeResolver.PlatformMode,
            walletEncryptionKey: "too-short",
            blockchainMode: "Live",
            environmentName: "Production");

        var capabilities = manager.GetCapabilities();

        capabilities.CustodyAvailable.Should().BeFalse();
        capabilities.UnavailableReason.Should().NotContain("WalletEncryptionKey");
    }

    // ── Fixtures ───────────────────────────────────────────────────────────

    private Guid SeedApprovedSubject(Guid tenantId) => SeedSubject(tenantId, KycStatus.APPROVED);

    private Guid SeedSubject(Guid tenantId, KycStatus status)
    {
        var userId = Guid.NewGuid();
        _tenants.Setup(manager => manager.ResolveChildAsync(
                tenantId, ExternalSubject, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ChildUserResponse>.Success(new ChildUserResponse
            {
                UserId = userId,
                ExternalUserId = ExternalSubject
            }));
        _kyc.Setup(manager => manager.GetStatusAsync(userId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmissionModel>.Success(new KycSubmissionModel
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantId = tenantId,
                Status = status,
                SubmittedAt = DateTime.UtcNow.AddMinutes(-10)
            }));
        _wallets.Setup(manager => manager.QueryAsync(
                It.IsAny<WalletQueryRequest>(), userId, It.IsAny<AZOARequest?>()))
            .ReturnsAsync(AZOAResult<IEnumerable<IWallet>>.Success(new IWallet[]
            {
                new Wallet
                {
                    Id = WalletBootstrapIdentity.For(userId, "Algorand"),
                    UserId = userId,
                    ChainType = "Algorand",
                    Address = "ALGOADDRESS",
                    WalletType = WalletType.Platform,
                    EncryptedPrivateKey = "ciphertext"
                }
            }));
        return userId;
    }

    private TenantCustodialAccountManager CreateManager(
        string walletEncryptionKey = "a-development-custody-secret-that-is-long-enough",
        string blockchainMode = "Simulated",
        string custodyMode = CustodyModeResolver.DevelopmentOnlyMode,
        string environmentName = "Development",
        string? manualReviewSlaHours = null,
        bool registerScreeningStore = true)
    {
        var kycCapabilities = new KycProviderCapabilitiesModel
        {
            Provider = KycProvider.IDENTOMAT,
            ProviderKey = "identomat",
            Available = true,
            HostedVerification = true
        };
        _kyc.Setup(manager => manager.GetCapabilities()).Returns(kycCapabilities);
        _kyc.Setup(manager => manager.GetCapabilitiesAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycProviderCapabilitiesModel>.Success(kycCapabilities));
        _providers.Setup(factory => factory.GetProvider("Algorand", ChainNetwork.Devnet))
            .Returns(Mock.Of<IBlockchainProvider>());

        var settings = new Dictionary<string, string?>
        {
            ["AZOA:WalletEncryptionKey"] = walletEncryptionKey,
            ["Blockchain:DefaultChain"] = "Algorand",
            ["Blockchain:DefaultNetwork"] = "Devnet",
            ["Blockchain:Mode"] = blockchainMode,
            ["CustodialAccounts:Enabled"] = "true",
            ["CustodialAccounts:WalletChain"] = "Algorand",
            ["CustodialAccounts:CustodyMode"] = custodyMode
        };
        if (manualReviewSlaHours is not null)
            settings[KycAmlClearance.ManualReviewSlaHoursConfigurationPath] = manualReviewSlaHours;

        return new TenantCustodialAccountManager(
            _tenants.Object,
            _wallets.Object,
            _kyc.Object,
            _providers.Object,
            _idempotency.Object,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == environmentName),
            registerScreeningStore ? _screening : null);
    }
}
