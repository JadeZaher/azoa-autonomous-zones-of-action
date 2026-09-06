using System.Text.Json;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions.Handlers;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions.Handlers;

/// <summary>
/// Bridge/Back Tier-2 fractionalization node handlers (final-hardening D1). Assert:
/// the tier flag + node type; routing through the REAL <see cref="ICrossChainBridgeService"/>
/// (no fabricated success — a service error maps to a Failed node); the
/// run-context-actor invariant (the config body carries no user); the
/// idempotency seed is forwarded to the bridge as the client key; and Back's
/// required-field fail-closed guards.
/// </summary>
public class BridgeBackNodeHandlerTests
{
    private static ActionEntity ActionWithUserAndNode(Guid userId, ActionNode node) =>
        new() { Id = Guid.NewGuid(), UserId = userId, Nodes = { node } };

    private static ActionNode NodeWith(ActionNodeType type, string config) =>
        new() { Id = Guid.NewGuid(), NodeType = type, Config = config };

    private static ActionNodeExecutionContext CtxFor(ActionNode node, Guid userId, Guid runId) =>
        new(runId, node.Id, ActionWithUserAndNode(userId, node), actingUserId: userId);

    // ─── Bridge ───

    [Fact]
    public void BridgeNodeHandler_DeclaresChainCapabilityAndType()
    {
        var handler = new BridgeNodeHandler(new Mock<ICrossChainBridgeService>().Object);
        handler.NodeType.Should().Be(ActionNodeType.Bridge);
        ((IActionNodeHandler)handler).RequiresChainCapability.Should().BeTrue();
    }

    [Fact]
    public void BridgeNodeConfig_AmountRoundTripsAsExactDecimalString()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(
            new BridgeNodeConfig { Amount = ulong.MaxValue },
            options);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("amount").GetString()
            .Should().Be("18446744073709551615");
        JsonSerializer.Deserialize<BridgeNodeConfig>(json, options)!.Amount
            .Should().Be(RailAmount.OfChainAsset(ulong.MaxValue));
    }

    [Fact]
    public async Task BridgeNodeHandler_RoutesThroughRealBridge_WithRunContextUser_AndIdempotencySeed()
    {
        var runUser = Guid.NewGuid();
        var runId = Guid.NewGuid();

        Guid capturedUser = Guid.Empty;
        string? capturedIdemKey = null;
        var svc = new Mock<ICrossChainBridgeService>();
        svc.Setup(s => s.InitiateBridgeAsync(
                "Algorand", "Solana", "555", "recipient", It.IsAny<Guid>(), RailAmount.OfChainAsset(3),
                It.IsAny<BridgeMode?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
           .Callback<string, string, string, string, Guid, RailAmount, BridgeMode?, CancellationToken, string?>(
                (_, _, _, _, av, _, _, _, key) => { capturedUser = av; capturedIdemKey = key; })
           .ReturnsAsync(new AZOAResult<BridgeTransactionResult>
           {
               Result = new BridgeTransactionResult { Id = "bridge_1", LockTxHash = "0xLOCK" }
           });

        var handler = new BridgeNodeHandler(svc.Object);
        var cfg = new BridgeNodeConfig
        {
            SourceChain = "Algorand", TargetChain = "Solana",
            TokenId = "555", RecipientAddress = "recipient", Amount = 3
        };
        var node = NodeWith(ActionNodeType.Bridge, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, runUser, runId));

        result.IsError.Should().BeFalse();
        result.TxHash.Should().Be("0xLOCK");
        capturedUser.Should().Be(runUser, because: "the actor is the run-context user, never the config body");
        capturedIdemKey.Should().Be($"{runId}:{node.Id}", because: "the (run,node) idempotency seed is forwarded to the bridge");
    }

    [Fact]
    public async Task BridgeNodeHandler_ServiceError_MapsToFailed_NoFabricatedSuccess()
    {
        // Fail-closed path (e.g. Solana leg / kill switch): the bridge returns an
        // error and the node MUST surface it as Failed, never fabricate success.
        var svc = new Mock<ICrossChainBridgeService>();
        svc.Setup(s => s.InitiateBridgeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Guid>(), It.IsAny<RailAmount>(), It.IsAny<BridgeMode?>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
           .ReturnsAsync(new AZOAResult<BridgeTransactionResult> { IsError = true, Message = "Solana bridging not implemented (fail-closed)" });

        var handler = new BridgeNodeHandler(svc.Object);
        var node = NodeWith(ActionNodeType.Bridge, JsonSerializer.Serialize(new BridgeNodeConfig()));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid(), Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("fail-closed");
    }

    [Fact]
    public async Task BridgeNodeHandler_UnknownMode_FailsClosed_NoBridgeCall()
    {
        var svc = new Mock<ICrossChainBridgeService>();
        var handler = new BridgeNodeHandler(svc.Object);
        var cfg = new BridgeNodeConfig { Mode = "Teleport" };
        var node = NodeWith(ActionNodeType.Bridge, JsonSerializer.Serialize(cfg));

        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid(), Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("unknown bridge mode");
        svc.Verify(s => s.InitiateBridgeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Guid>(), It.IsAny<RailAmount>(), It.IsAny<BridgeMode?>(),
            It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Never);
    }

    // ─── Back ───

    [Fact]
    public void BackNodeHandler_DeclaresChainCapabilityAndType()
    {
        var handler = new BackNodeHandler(new Mock<ICrossChainBridgeService>().Object);
        handler.NodeType.Should().Be(ActionNodeType.Back);
        ((IActionNodeHandler)handler).RequiresChainCapability.Should().BeTrue();
    }

    [Fact]
    public async Task BackNodeHandler_RoutesReverse_IdorScopedToRunUser_WithIdempotencySeed()
    {
        var runUser = Guid.NewGuid();
        var runId = Guid.NewGuid();

        Guid? capturedCaller = null;
        string? capturedIdemKey = null;
        var svc = new Mock<ICrossChainBridgeService>();
        svc.Setup(s => s.ReverseBridgeAsync(
                "bridge_1", "sourceRecipient", It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<Guid?>()))
           .Callback<string, string, CancellationToken, string?, Guid?>(
                (_, _, _, key, caller) => { capturedIdemKey = key; capturedCaller = caller; })
           .ReturnsAsync(new AZOAResult<BridgeTransactionResult>
           {
               Result = new BridgeTransactionResult { Id = "bridge_1", RedemptionTxHash = "0xBURN", SourceChain = "Algorand" }
           });

        var handler = new BackNodeHandler(svc.Object);
        var cfg = new BackNodeConfig { BridgeTransactionId = "bridge_1", SourceRecipientAddress = "sourceRecipient" };
        var node = NodeWith(ActionNodeType.Back, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, runUser, runId));

        result.IsError.Should().BeFalse();
        result.TxHash.Should().Be("0xBURN");
        capturedCaller.Should().Be(runUser, because: "the reverse is IDOR-scoped to the run-context user's own bridge rows");
        capturedIdemKey.Should().Be($"{runId}:{node.Id}");
    }

    [Fact]
    public async Task BackNodeHandler_MissingBridgeTransactionId_FailsClosed_NoReverseCall()
    {
        var svc = new Mock<ICrossChainBridgeService>();
        var handler = new BackNodeHandler(svc.Object);
        var cfg = new BackNodeConfig { BridgeTransactionId = "", SourceRecipientAddress = "x" };
        var node = NodeWith(ActionNodeType.Back, JsonSerializer.Serialize(cfg));

        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid(), Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("BridgeTransactionId is required");
        svc.Verify(s => s.ReverseBridgeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task BackNodeHandler_ServiceError_MapsToFailed()
    {
        var svc = new Mock<ICrossChainBridgeService>();
        svc.Setup(s => s.ReverseBridgeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<BridgeTransactionResult> { IsError = true, Message = "only completed bridges can be reversed" });

        var handler = new BackNodeHandler(svc.Object);
        var cfg = new BackNodeConfig { BridgeTransactionId = "bridge_1", SourceRecipientAddress = "x" };
        var node = NodeWith(ActionNodeType.Back, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid(), Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("only completed bridges");
    }
}
