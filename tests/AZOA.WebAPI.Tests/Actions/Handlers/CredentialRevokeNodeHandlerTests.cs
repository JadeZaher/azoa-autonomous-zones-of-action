using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Actions.Handlers;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions.Handlers;

/// <summary>
/// D19 CredentialRevoke node handler — the Tier-2 tier flag, the settlement rail it
/// declares, the run-context actor invariant, and above all the rule that only a
/// CONFIRMED clawback is a node success. This codebase has shipped fabricated-success
/// bugs on value paths three times; the pending/unknown cases below are the guard.
/// </summary>
public class CredentialRevokeNodeHandlerTests
{
    private const string AssetId = "778899";
    private const string Chain = "Algorand";

    private static ActionNode NodeWith(string config) =>
        new() { Id = Guid.NewGuid(), NodeType = ActionNodeType.CredentialRevoke, Config = config };

    private static ActionNode NodeFor(CredentialRevokeNodeConfig cfg) =>
        NodeWith(JsonSerializer.Serialize(cfg));

    private static ActionNodeExecutionContext CtxFor(ActionNode node, Guid userId, Guid? tenantId = null) =>
        new(Guid.NewGuid(), node.Id,
            new ActionEntity { Id = Guid.NewGuid(), UserId = userId, Nodes = { node } },
            actingUserId: userId,
            actingTenantId: tenantId);

    private static CredentialRevokeResult ResultWith(string status, string? txHash) => new()
    {
        AssetId = AssetId,
        ChainType = Chain,
        Status = status,
        TxHash = txHash,
    };

    private static Mock<ICredentialManager> ManagerReturning(AZOAResult<CredentialRevokeResult> outcome)
    {
        var mgr = new Mock<ICredentialManager>();
        mgr.Setup(m => m.RevokeAsync(
                It.IsAny<string>(), It.IsAny<CredentialRevokeRequest>(), It.IsAny<Guid>(),
                It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(outcome);
        return mgr;
    }

    // ── Tier + rail declarations (what the pre-execution gate reads) ──────────

    [Fact]
    public void DeclaresChainCapabilityAndType()
    {
        var handler = new CredentialRevokeNodeHandler(Mock.Of<ICredentialManager>());

        handler.NodeType.Should().Be(ActionNodeType.CredentialRevoke);
        ((IActionNodeHandler)handler).RequiresChainCapability.Should().BeTrue(
            "a revocation reaches an on-chain sign; without the flag the rail gate never fires");
    }

    [Fact]
    public void ResolveSettlementRailId_ReadsConfiguredChain()
    {
        var handler = new CredentialRevokeNodeHandler(Mock.Of<ICredentialManager>());
        var node = NodeFor(new CredentialRevokeNodeConfig { ChainType = "Algorand", AssetId = AssetId });

        ((IActionNodeHandler)handler).ResolveSettlementRailId(node).Should().Be("Algorand");
    }

    [Fact]
    public void ResolveSettlementRailId_UnparseableConfig_ReturnsNull()
    {
        var handler = new CredentialRevokeNodeHandler(Mock.Of<ICredentialManager>());

        // Null ⇒ "the platform default rail"; the gate still runs, it just asks about
        // the default. It must never throw out of a pre-execution declaration.
        ((IActionNodeHandler)handler).ResolveSettlementRailId(NodeWith("{ not json"))
            .Should().BeNull();
    }

    // ── Config validation ─────────────────────────────────────────────────────

    [Fact]
    public async Task MissingAssetId_IsTerminallyInvalid_AndNeverCallsTheManager()
    {
        var mgr = new Mock<ICredentialManager>();
        var handler = new CredentialRevokeNodeHandler(mgr.Object);
        var node = NodeFor(new CredentialRevokeNodeConfig { AssetId = "" });

        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("AssetId");
        mgr.VerifyNoOtherCalls();
    }

    // ── Actor invariant ───────────────────────────────────────────────────────

    [Fact]
    public async Task Revokes_AsRunContextUser_WithRunNodeIdempotencySeed()
    {
        var runUser = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        Guid capturedUser = Guid.Empty;
        string? capturedKey = null;
        Guid? capturedTenant = null;

        var mgr = new Mock<ICredentialManager>();
        mgr.Setup(m => m.RevokeAsync(
                AssetId, It.IsAny<CredentialRevokeRequest>(), It.IsAny<Guid>(),
                It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
           .Callback<string, CredentialRevokeRequest, Guid, string?, Guid?, CancellationToken>(
               (_, _, user, key, tenant, _) => { capturedUser = user; capturedKey = key; capturedTenant = tenant; })
           .ReturnsAsync(new AZOAResult<CredentialRevokeResult>
           {
               Result = ResultWith(OperationStatus.Completed, "TX-OK")
           });

        var handler = new CredentialRevokeNodeHandler(mgr.Object);
        var node = NodeFor(new CredentialRevokeNodeConfig { AssetId = AssetId, ChainType = Chain });
        var ctx = CtxFor(node, runUser, tenantId);

        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        capturedUser.Should().Be(runUser, "the config carries no user; the actor is always the run context");
        capturedTenant.Should().Be(tenantId);
        // Stable (run, node) seed ⇒ a re-evaluation dedupes to ONE irreversible clawback.
        capturedKey.Should().Be($"{ctx.RunId}:{ctx.NodeId}");
    }

    // ── Outcome mapping: only a CONFIRMED clawback is a success ───────────────

    [Fact]
    public async Task ConfirmedRevocation_Succeeds_AndForwardsTxHash()
    {
        var mgr = ManagerReturning(new AZOAResult<CredentialRevokeResult>
        {
            Result = ResultWith(OperationStatus.Completed, "TX-CONFIRMED")
        });
        var handler = new CredentialRevokeNodeHandler(mgr.Object);
        var node = NodeFor(new CredentialRevokeNodeConfig { AssetId = AssetId, ChainType = Chain });

        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        result.TxHash.Should().Be("TX-CONFIRMED");
        result.ChainType.Should().Be(Chain);
    }

    [Fact]
    public async Task PendingConfirmation_IsNotSuccess_AndKeepsTxHashForReconciliation()
    {
        var mgr = ManagerReturning(new AZOAResult<CredentialRevokeResult>
        {
            Result = ResultWith(OperationStatus.PendingConfirmation, "TX-PENDING"),
            Message = "submitted but not confirmed",
        });
        var handler = new CredentialRevokeNodeHandler(mgr.Object);
        var node = NodeFor(new CredentialRevokeNodeConfig { AssetId = AssetId, ChainType = Chain });

        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue("a broadcast that has not confirmed is not a revocation");
        result.Message.Should().Contain(OperationStatus.PendingConfirmation);
        // The hash MUST survive the failure or reconcile-before-retry blind-retries and
        // claws the credential back twice.
        result.TxHash.Should().Be("TX-PENDING");
        result.ChainType.Should().Be(Chain);
    }

    [Fact]
    public async Task UnknownOutcome_IsNotSuccess()
    {
        var mgr = ManagerReturning(new AZOAResult<CredentialRevokeResult>
        {
            Result = ResultWith(OperationStatus.Unknown, null),
            Message = "no transaction id",
        });
        var handler = new CredentialRevokeNodeHandler(mgr.Object);
        var node = NodeFor(new CredentialRevokeNodeConfig { AssetId = AssetId, ChainType = Chain });

        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain(OperationStatus.Unknown);
    }

    [Fact]
    public async Task ManagerFailureAfterBroadcast_ForwardsTxHashOnTheFailurePath()
    {
        var mgr = ManagerReturning(AZOAResult<CredentialRevokeResult>.Failure(
            "clawback rejected", ResultWith(OperationStatus.Failed, "TX-BROADCAST")));
        var handler = new CredentialRevokeNodeHandler(mgr.Object);
        var node = NodeFor(new CredentialRevokeNodeConfig { AssetId = AssetId, ChainType = Chain });

        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("clawback rejected");
        result.TxHash.Should().Be("TX-BROADCAST");
    }

    [Fact]
    public async Task ManagerRefusalWithNoResult_FailsWithoutTxHash()
    {
        var mgr = ManagerReturning(AZOAResult<CredentialRevokeResult>.Failure("no recorded issuer"));
        var handler = new CredentialRevokeNodeHandler(mgr.Object);
        var node = NodeFor(new CredentialRevokeNodeConfig { AssetId = AssetId, ChainType = Chain });

        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("no recorded issuer");
        result.TxHash.Should().BeNull();
    }

    /// <summary>
    /// The manager now returns a broadcast-but-unconfirmed revoke with IsError = true
    /// (C5), so the node sees it on the error branch rather than the status branch. It
    /// must STILL fail the node and STILL forward the tx hash: losing the hash here is
    /// what turns a reconcile into a blind retry and claws the credential back twice.
    /// </summary>
    [Fact]
    public async Task PendingConfirmationReportedAsError_FailsTheNode_AndStillForwardsTxHash()
    {
        var mgr = ManagerReturning(AZOAResult<CredentialRevokeResult>.FailureWithCode(
            "submitted but has NOT confirmed",
            CredentialRevokeCodes.PendingConfirmation,
            ResultWith(OperationStatus.PendingConfirmation, "TX-PENDING")));
        var handler = new CredentialRevokeNodeHandler(mgr.Object);
        var node = NodeFor(new CredentialRevokeNodeConfig { AssetId = AssetId, ChainType = Chain });

        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue("an unconfirmed broadcast must never advance a run");
        result.TxHash.Should().Be("TX-PENDING");
        result.ChainType.Should().Be(Chain);
    }
}

/// <summary>
/// D19 authorization + fail-closed rules on <see cref="CredentialManager"/>: the issuer
/// is the only party who may revoke, an unrecorded issuer is unrevocable, and no chain
/// call is reached until both are satisfied.
/// </summary>
/// <remarks>
/// These live beside the handler tests only because the D19 lane owns this one test
/// file; they belong in <c>tests/AZOA.WebAPI.Tests/Managers/CredentialManagerTests.cs</c>
/// and should move there with the next pass.
/// </remarks>
public class CredentialRevokeAuthorizationTests
{
    private const string AssetId = "778899";
    private const string Chain = "Algorand";

    private readonly Mock<ICredentialIssuerStore> _credentials = new();
    private readonly Mock<IWalletStore> _walletStore = new();
    private readonly Mock<IBlockchainProviderFactory> _factory = new();
    private readonly Mock<IBlockchainProvider> _provider = new();
    private readonly Mock<IAlgorandASAModule> _asa = new();
    private readonly Mock<IBlockchainOperationStore> _operations = new();
    private readonly Mock<IValueAccessService> _valueAccess = new();
    private readonly InMemoryIdempotencyStore _idempotency = new();

    private readonly Guid _issuerUserId = Guid.NewGuid();
    private readonly Guid _holderUserId = Guid.NewGuid();

    public CredentialRevokeAuthorizationTests()
    {
        _valueAccess
            .Setup(v => v.RequireValueAccessAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });

        _operations
            .Setup(o => o.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IBlockchainOperation op, CancellationToken _) =>
                new AZOAResult<IBlockchainOperation> { Result = op });

        _factory.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(_provider.Object);
        var module = _asa.Object;
        _provider.Setup(p => p.TryGetModule<IAlgorandASAModule>(out It.Ref<IAlgorandASAModule?>.IsAny))
                 .Returns((out IAlgorandASAModule? m) => { m = module; return true; });

        _walletStore.Setup(w => w.GetByUserAsync(_issuerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>>
            {
                Result = new[] { WalletFor(_issuerUserId, Chain, "ISSUERADDRESS") }
            });

        // Ownership bookkeeping succeeds by default; the tests that care about it
        // either count the call or make it fail on purpose.
        _credentials
            .Setup(c => c.RecordCredentialRevokedAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
    }

    private static IWallet WalletFor(Guid userId, string chain, string address) =>
        Mock.Of<IWallet>(w => w.Id == Guid.NewGuid() && w.UserId == userId
                              && w.ChainType == chain && w.Address == address);

    private CredentialManager BuildManager() => new(
        _credentials.Object, _walletStore.Object, _factory.Object,
        _operations.Object, _idempotency, _valueAccess.Object);

    private void CredentialIs(Guid? issuerUserId, bool isSoulbound = true, string? holderAddress = "HOLDERADDRESS")
        => CredentialIsFor(AssetId, issuerUserId, isSoulbound, holderAddress);

    /// <summary>Registers one credential row and returns its id, so an ownership
    /// assertion can name the exact row a confirmed revocation must re-point.</summary>
    private Guid CredentialIsFor(
        string assetId, Guid? issuerUserId, bool isSoulbound = true, string? holderAddress = "HOLDERADDRESS")
    {
        var credentialId = Guid.NewGuid();
        _credentials
            .Setup(c => c.FindCurrentByAssetIdAsync(Chain, assetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<CredentialOwnershipRecord?>
            {
                Result = new CredentialOwnershipRecord
                {
                    Id = credentialId,
                    HolderUserId = _holderUserId,
                    HolderAddress = holderAddress,
                    IssuerUserId = issuerUserId,
                    IsSoulbound = isSoulbound,
                    ChainType = Chain,
                    TokenId = assetId,
                }
            });
        return credentialId;
    }

    /// <summary>Captures every operation row the manager writes, newest last.</summary>
    private List<IBlockchainOperation> CaptureOperations()
    {
        var written = new List<IBlockchainOperation>();
        _operations
            .Setup(o => o.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
            .Callback<IBlockchainOperation, CancellationToken>((op, _) => written.Add(op))
            .ReturnsAsync((IBlockchainOperation op, CancellationToken _) =>
                new AZOAResult<IBlockchainOperation> { Result = op });
        return written;
    }

    private void VerifyOwnershipMoved(Guid credentialId, Times times)
        => _credentials.Verify(c => c.RecordCredentialRevokedAsync(
            credentialId, _issuerUserId, "ISSUERADDRESS", It.IsAny<CancellationToken>()), times);

    private static AZOAResult<string> Confirmed(string txId)
        => new() { Result = txId, Message = "confirmed in round 42" };

    private static AZOAResult<string> Broadcast(string txId)
        => new()
        {
            Result = txId,
            Message = $"{OperationStatus.PendingConfirmationMarker}: Algorand revoke",
        };

    private void ClawbackReturns(AZOAResult<string> outcome)
        => _asa.Setup(a => a.ClawbackAssetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<RailAmount>(), It.IsAny<SigningContext>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(outcome);

    private void VerifyNoClawback()
        => _asa.Verify(a => a.ClawbackAssetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<RailAmount>(), It.IsAny<SigningContext>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

    private Task<AZOAResult<CredentialRevokeResult>> Revoke(
        Guid callerUserId, string? key = "k", string? assetId = null)
        => BuildManager().RevokeAsync(
            assetId ?? AssetId, new CredentialRevokeRequest { ChainType = Chain }, callerUserId, key);

    // ── The keystone: issuer yes, holder no ───────────────────────────────────

    [Fact]
    public async Task Holder_CannotRevoke_TheirOwnCredential()
    {
        CredentialIs(_issuerUserId);

        var result = await Revoke(_holderUserId);

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.Forbidden);
        result.Message.Should().Contain("issuer");
        VerifyNoClawback();
    }

    [Fact]
    public async Task ThirdParty_CannotRevoke()
    {
        CredentialIs(_issuerUserId);

        var result = await Revoke(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.Forbidden);
        VerifyNoClawback();
    }

    [Fact]
    public async Task UnrecordedIssuer_FailsClosed_AndIsNeverInferredFromTheHolder()
    {
        // Every row minted before D19 looks exactly like this. Inferring the issuer
        // from CurrentOwner would let the holder revoke, which is the inversion D19
        // exists to prevent — so the ONLY correct behaviour is refusal.
        CredentialIs(issuerUserId: null);

        var byHolder = await Revoke(_holderUserId, key: "a");
        var byIssuer = await Revoke(_issuerUserId, key: "b");

        byHolder.IsError.Should().BeTrue();
        byIssuer.IsError.Should().BeTrue();
        byIssuer.Message.Should().Contain("no recorded issuer");
        VerifyNoClawback();
    }

    [Fact]
    public async Task NonSoulboundAsset_IsRefused()
    {
        CredentialIs(_issuerUserId, isSoulbound: false);

        var result = await Revoke(_issuerUserId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not soulbound");
        VerifyNoClawback();
    }

    [Fact]
    public async Task MissingHolderAddress_IsRefused_RatherThanGuessed()
    {
        CredentialIs(_issuerUserId, holderAddress: null);

        var result = await Revoke(_issuerUserId);

        result.IsError.Should().BeTrue();
        VerifyNoClawback();
    }

    [Fact]
    public async Task IssuerWithNoWalletOnTheChain_IsRefused()
    {
        CredentialIs(_issuerUserId);
        _walletStore.Setup(w => w.GetByUserAsync(_issuerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = Array.Empty<IWallet>() });

        var result = await Revoke(_issuerUserId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("wallet");
        VerifyNoClawback();
    }

    [Fact]
    public async Task LookupError_FailsClosed_RatherThanTreatingTheCredentialAsAbsent()
    {
        _credentials
            .Setup(c => c.FindCurrentByAssetIdAsync(Chain, AssetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<CredentialOwnershipRecord?>.Failure("database unavailable"));

        var result = await Revoke(_issuerUserId);

        result.IsError.Should().BeTrue();
        VerifyNoClawback();
    }

    // ── Chain-outcome mapping ─────────────────────────────────────────────────

    [Fact]
    public async Task Issuer_ConfirmedClawback_IsCompleted_AndClawsFromHolderToIssuer()
    {
        CredentialIs(_issuerUserId);
        string? fromAddress = null;
        string? toAddress = null;
        _asa.Setup(a => a.ClawbackAssetAsync(
                AssetId, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<RailAmount>(), It.IsAny<SigningContext>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, RailAmount, SigningContext, string?, CancellationToken>(
                (_, from, to, _, _, _, _) => { fromAddress = from; toAddress = to; })
            .ReturnsAsync(new AZOAResult<string> { Result = "TX-1", Message = "confirmed in round 42" });

        var result = await Revoke(_issuerUserId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(OperationStatus.Completed);
        result.Result.IsConfirmed.Should().BeTrue();
        result.Result.TxHash.Should().Be("TX-1");
        fromAddress.Should().Be("HOLDERADDRESS", "the asset is taken from the holder");
        toAddress.Should().Be("ISSUERADDRESS", "and returned to the issuer");
    }

    [Fact]
    public async Task BroadcastButUnconfirmed_IsPendingConfirmation_NotCompleted()
    {
        CredentialIs(_issuerUserId);
        ClawbackReturns(new AZOAResult<string>
        {
            Result = "TX-2",
            Message = $"{OperationStatus.PendingConfirmationMarker}: Algorand revoke ASA {AssetId}",
        });

        var result = await Revoke(_issuerUserId);

        result.Result!.Status.Should().Be(OperationStatus.PendingConfirmation);
        result.Result.IsConfirmed.Should().BeFalse();
        result.Result.TxHash.Should().Be("TX-2");
    }

    [Fact]
    public async Task BroadcastButUnconfirmed_LeavesTheClaimInProgress_SoADuplicateDoesNotReBroadcast()
    {
        CredentialIs(_issuerUserId);
        ClawbackReturns(new AZOAResult<string>
        {
            Result = "TX-2",
            Message = $"{OperationStatus.PendingConfirmationMarker}: Algorand revoke ASA {AssetId}",
        });

        await Revoke(_issuerUserId, key: "same");
        var duplicate = await Revoke(_issuerUserId, key: "same");

        duplicate.IsError.Should().BeTrue("an unsettled irreversible effect must not be repeated");
        duplicate.Message.Should().Contain("in progress");
        _asa.Verify(a => a.ClawbackAssetAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<RailAmount>(), It.IsAny<SigningContext>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SuccessWithoutATransactionId_IsUnknown_NotCompleted()
    {
        CredentialIs(_issuerUserId);
        ClawbackReturns(new AZOAResult<string> { Result = null, Message = "done" });

        var result = await Revoke(_issuerUserId);

        result.IsError.Should().BeTrue("a success with no transaction id is evidence of nothing");
        result.Result!.Status.Should().Be(OperationStatus.Unknown);
    }

    [Fact]
    public async Task ConfirmedRevocation_RecordsACredentialRevokeOperationForAnalytics()
    {
        CredentialIs(_issuerUserId);
        ClawbackReturns(new AZOAResult<string> { Result = "TX-3", Message = "confirmed in round 7" });

        var written = new List<IBlockchainOperation>();
        _operations
            .Setup(o => o.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
            .Callback<IBlockchainOperation, CancellationToken>((op, _) => written.Add(op))
            .ReturnsAsync((IBlockchainOperation op, CancellationToken _) =>
                new AZOAResult<IBlockchainOperation> { Result = op });

        await Revoke(_issuerUserId);

        var final = written.Last();
        final.OperationType.Should().Be(CredentialManager.OperationTypeName);
        final.Status.Should().Be(OperationStatus.Completed);
        final.UserId.Should().Be(_issuerUserId);
        final.Parameters["TxHash"].Should().Be("TX-3");
        final.Parameters["HolderAddress"].Should().Be("HOLDERADDRESS");
        // One indivisible credential — the amount is a RailAmount, never a raw count.
        final.Parameters["Amount"].Should().Be(RailAmount.OfChainAsset(1).ToBaseUnitString());
    }

    [Fact]
    public async Task TenantDrivenRevocation_StampsTheActingTenantAndItsOwnSigningScope()
    {
        CredentialIs(_issuerUserId);
        ClawbackReturns(new AZOAResult<string> { Result = "TX-4", Message = "confirmed in round 9" });
        var tenantId = Guid.NewGuid();

        var written = new List<IBlockchainOperation>();
        _operations
            .Setup(o => o.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
            .Callback<IBlockchainOperation, CancellationToken>((op, _) => written.Add(op))
            .ReturnsAsync((IBlockchainOperation op, CancellationToken _) =>
                new AZOAResult<IBlockchainOperation> { Result = op });

        await BuildManager().RevokeAsync(
            AssetId, new CredentialRevokeRequest { ChainType = Chain }, _issuerUserId, "t", tenantId);

        var final = written.Last();
        final.ActingTenantId.Should().Be(tenantId);
        final.SigningScope.Should().Be(AzoaScopes.CredentialRevokeSign);
        // The S6 guardrail must accept the pairing, or the signing seam refuses to
        // build a tenant-driven context and no revocation can ever be delegated.
        AzoaScopes.IsScopeValidForOperation(
            CredentialManager.OperationTypeName, AzoaScopes.CredentialRevokeSign).Should().BeTrue();
        // …and must not accept a neighbouring value scope in its place.
        AzoaScopes.IsScopeValidForOperation(
            CredentialManager.OperationTypeName, AzoaScopes.TransferSign).Should().BeFalse();
    }

    // ── C3: a confirmed revocation must move ownership ────────────────────────

    /// <summary>
    /// Recording only the BlockchainOperation row leaves the live nft_ownership row
    /// naming the original holder with is_current = true, so every AZOA read surface
    /// keeps reporting the credential as held by the party it was just taken from.
    /// Delete the RecordCredentialRevokedAsync call in the manager and this fails.
    /// </summary>
    [Fact]
    public async Task ConfirmedRevocation_MovesOwnershipToTheIssuer()
    {
        var credentialId = CredentialIsFor(AssetId, _issuerUserId);
        ClawbackReturns(Confirmed("TX-OWN"));

        var result = await Revoke(_issuerUserId);

        result.Result!.Status.Should().Be(OperationStatus.Completed);
        VerifyOwnershipMoved(credentialId, Times.Once());
    }

    [Fact]
    public async Task BroadcastButUnconfirmed_LeavesOwnershipAlone()
    {
        var credentialId = CredentialIsFor(AssetId, _issuerUserId);
        ClawbackReturns(Broadcast("TX-PEND"));

        await Revoke(_issuerUserId);

        VerifyOwnershipMoved(credentialId, Times.Never());
    }

    [Fact]
    public async Task FailedClawback_LeavesOwnershipAlone()
    {
        var credentialId = CredentialIsFor(AssetId, _issuerUserId);
        ClawbackReturns(AZOAResult<string>.Failure("clawback rejected"));

        await Revoke(_issuerUserId);

        VerifyOwnershipMoved(credentialId, Times.Never());
    }

    /// <summary>
    /// The clawback is irreversible, so a failed bookkeeping write cannot turn the
    /// outcome back into a failure — a retry would attempt a SECOND clawback. It must
    /// stay Completed and say loudly that the record drifted.
    /// </summary>
    [Fact]
    public async Task OwnershipWriteFailure_StillReportsTheConfirmedRevocation_ButLoudly()
    {
        CredentialIsFor(AssetId, _issuerUserId);
        ClawbackReturns(Confirmed("TX-DRIFT"));
        _credentials
            .Setup(c => c.RecordCredentialRevokedAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Failure("surreal unavailable"));
        var written = CaptureOperations();

        var result = await Revoke(_issuerUserId);

        result.IsError.Should().BeFalse("the chain confirmed; a retry would claw the asset back twice");
        result.Result!.Status.Should().Be(OperationStatus.Completed);
        result.Message.Should().Contain("WARNING");
        written.Last().Parameters["OwnershipUpdateError"].Should().Contain("surreal unavailable");
    }

    // ── C4: the idempotency key names the credential ──────────────────────────

    /// <summary>
    /// The repro: one client Idempotency-Key header, two different credentials. A key
    /// that omitted the asset replayed the FIRST credential's Completed result and
    /// reported the second one revoked when nothing had touched it.
    /// </summary>
    [Fact]
    public async Task SameClientKey_DifferentCredential_IsNotAReplay()
    {
        const string otherAssetId = "112233";
        CredentialIsFor(AssetId, _issuerUserId);
        CredentialIsFor(otherAssetId, _issuerUserId);
        ClawbackReturns(Confirmed("TX-A"));

        var first = await Revoke(_issuerUserId, key: "same-header");
        var second = await Revoke(_issuerUserId, key: "same-header", assetId: otherAssetId);

        first.Result!.AssetId.Should().Be(AssetId);
        second.IsError.Should().BeFalse();
        second.Result!.AssetId.Should().Be(otherAssetId,
            "the second credential must get its OWN outcome, not the first one's");
        second.Result.Replayed.Should().BeFalse();
        _asa.Verify(a => a.ClawbackAssetAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<RailAmount>(), It.IsAny<SigningContext>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SameClientKey_SameCredential_StillReplaysWithoutASecondClawback()
    {
        CredentialIsFor(AssetId, _issuerUserId);
        ClawbackReturns(Confirmed("TX-ONCE"));

        await Revoke(_issuerUserId, key: "same-header");
        var duplicate = await Revoke(_issuerUserId, key: "same-header");

        duplicate.IsError.Should().BeFalse();
        duplicate.Result!.Replayed.Should().BeTrue();
        duplicate.Result.AssetId.Should().Be(AssetId);
        _asa.Verify(a => a.ClawbackAssetAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<RailAmount>(), It.IsAny<SigningContext>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Defence in depth behind the key itself: even if a record for this key somehow
    /// carries another credential's payload, replaying it would report the wrong asset
    /// as revoked. Refuse instead of serving a wrong answer.
    /// </summary>
    [Fact]
    public async Task ReplayCarryingADifferentCredential_IsRefused_NotServed()
    {
        CredentialIsFor(AssetId, _issuerUserId);
        ClawbackReturns(AZOAResult<string>.Failure("first attempt rejected"));

        // One run claims (and settles) the key, which is how the test learns it
        // without duplicating the manager's key format.
        await Revoke(_issuerUserId, key: "poisoned");
        var key = _idempotency.ClaimedKeys.Should().ContainSingle().Subject;

        // Serialized through the PRODUCTION replay serializer, so the payload is exactly
        // what the manager would have cached — a hand-rolled JSON shape could deserialize
        // to an empty result and pass this test for the wrong reason.
        _idempotency.SeedCompleted(key, IdempotencyReplay.SerializeForReplay(new CredentialRevokeResult
        {
            AssetId = "999999",
            Status = OperationStatus.Completed,
            TxHash = "TX-OTHER",
        }));

        var replay = await Revoke(_issuerUserId, key: "poisoned");

        replay.IsError.Should().BeTrue();
        replay.Code.Should().Be(AzoaErrorCodes.Conflict);
        replay.Message.Should().Contain("999999");
    }

    // ── C5 + C6: an unconfirmed broadcast is unmistakable, and settleable ──────

    /// <summary>
    /// IsError — not a nested Status string — is what every AZOA client helper branches
    /// on. A 200/IsError=false response to an unconfirmed clawback tells every one of
    /// them the credential was revoked when the chain has said no such thing.
    /// </summary>
    [Fact]
    public async Task BroadcastButUnconfirmed_IsAnErrorToTheClient_WithItsOwnCode()
    {
        CredentialIsFor(AssetId, _issuerUserId);
        ClawbackReturns(Broadcast("TX-UNCONFIRMED"));

        var result = await Revoke(_issuerUserId);

        result.IsError.Should().BeTrue("a helper that branches on IsError must not read this as success");
        result.Code.Should().Be(CredentialRevokeCodes.PendingConfirmation);
        result.Result!.Status.Should().Be(OperationStatus.PendingConfirmation);
        result.Result.IsConfirmed.Should().BeFalse();
        result.Result.TxHash.Should().Be("TX-UNCONFIRMED", "reconciliation needs the hash");
    }

    /// <summary>
    /// ReconciliationService settles a stranded claim from Parameters["IdempotencyKey"]
    /// and refuses to reconstruct a key it cannot read. Without the stamp the claim
    /// stays InProgress forever and the credential becomes permanently unrevocable by
    /// its own idempotency record.
    /// </summary>
    [Fact]
    public async Task PendingRevocation_StampsTheKeyAndReplayPayload_SoReconciliationCanSettleIt()
    {
        CredentialIsFor(AssetId, _issuerUserId);
        ClawbackReturns(Broadcast("TX-STRANDED"));
        var written = CaptureOperations();

        await Revoke(_issuerUserId);

        var final = written.Last();
        final.Status.Should().Be(OperationStatus.PendingConfirmation);
        final.Parameters["IdempotencyKey"].Should().Be(
            _idempotency.ClaimedKeys.Should().ContainSingle().Subject,
            "reconciliation resolves the claim from this parameter and never guesses one");
        final.Parameters[IdempotencyParameterNames.ResultPayload].Should().Contain(AssetId,
            "settling Completed must replay THIS revocation, not a fabricated payload");
    }

    [Fact]
    public async Task EveryOperationRow_CarriesItsClaimKey_EvenBeforeTheChainAnswers()
    {
        CredentialIsFor(AssetId, _issuerUserId);
        ClawbackReturns(AZOAResult<string>.Failure("rejected"));
        var written = CaptureOperations();

        await Revoke(_issuerUserId);

        written.Should().NotBeEmpty();
        written[0].Parameters["IdempotencyKey"].Should().NotBeNullOrWhiteSpace(
            "a crash between the first write and the chain call must still leave the key recoverable");
    }

    // ── In-memory idempotency store (faithful claim-once + replay) ─────────────

    private sealed class InMemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<string, IdempotencyRecord> _records = new(StringComparer.Ordinal);
        private readonly List<string> _claimedKeys = new();

        /// <summary>Every distinct key a claim was attempted under, in order. Lets a test
        /// assert on the manager's real key without restating its format.</summary>
        public IReadOnlyList<string> ClaimedKeys => _claimedKeys;

        /// <summary>Forces a key to a settled Completed record carrying an arbitrary
        /// payload — the only way to exercise a poisoned replay.</summary>
        public void SeedCompleted(string key, string resultPayload)
            => _records[key] = new IdempotencyRecord
            {
                Key = key,
                OperationType = CredentialManager.OperationTypeName,
                State = IdempotencyState.Completed,
                ResultPayload = resultPayload,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

        public Task<IdempotencyClaim> TryClaimAsync(string key, string operationType, CancellationToken ct)
        {
            if (!_claimedKeys.Contains(key, StringComparer.Ordinal))
                _claimedKeys.Add(key);

            if (_records.TryGetValue(key, out var existing))
                return Task.FromResult(new IdempotencyClaim(false, Clone(existing)));

            var record = new IdempotencyRecord
            {
                Key = key,
                OperationType = operationType,
                State = IdempotencyState.InProgress,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _records[key] = record;
            return Task.FromResult(new IdempotencyClaim(true, Clone(record)));
        }

        public Task CompleteAsync(string key, string resultPayload, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var record) && record.State == IdempotencyState.InProgress)
            {
                record.State = IdempotencyState.Completed;
                record.ResultPayload = resultPayload;
                record.UpdatedAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task FailAsync(string key, string error, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var record) && record.State == IdempotencyState.InProgress)
            {
                record.State = IdempotencyState.Failed;
                record.Error = error;
                record.UpdatedAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
            => Task.FromResult(_records.TryGetValue(key, out var r) ? Clone(r) : null);

        private static IdempotencyRecord Clone(IdempotencyRecord r) => new()
        {
            Key = r.Key,
            OperationType = r.OperationType,
            State = r.State,
            ResultPayload = r.ResultPayload,
            Error = r.Error,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt
        };
    }
}
