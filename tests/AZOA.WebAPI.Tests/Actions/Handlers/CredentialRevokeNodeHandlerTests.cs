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
