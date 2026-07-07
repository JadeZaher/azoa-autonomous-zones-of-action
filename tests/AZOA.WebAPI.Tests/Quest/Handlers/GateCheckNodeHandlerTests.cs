using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Handlers;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest.Handlers;

/// <summary>
/// Dispatch tests for <see cref="GateCheckNodeHandler"/>: Pass returns
/// <c>Ok {"pass":true}</c>, Fail returns <c>IsError</c> so the engine skips
/// downstream, a malformed predicate returns a <c>Fail</c> (never throws), and
/// the handler stays chain-free (<c>RequiresChainCapability == false</c>).
/// </summary>
public class GateCheckNodeHandlerTests
{
    private static (QuestEntity quest, Guid gateId) BuildQuest(
        string predicate, Dictionary<string, JsonElement>? reads,
        out QuestNode upstreamNode)
    {
        upstreamNode = new QuestNode { Id = Guid.NewGuid(), Name = "bal" };
        var cfg = new GateCheckNodeConfig
        {
            Predicate = predicate,
            Reads = reads ?? new Dictionary<string, JsonElement>()
        };
        var gateNode = new QuestNode
        {
            Id = Guid.NewGuid(),
            Name = "gate",
            NodeType = QuestNodeType.GateCheck,
            Config = JsonSerializer.Serialize(cfg)
        };
        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Nodes = new List<QuestNode> { upstreamNode, gateNode },
            Edges = new List<QuestEdge>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = upstreamNode.Id, TargetNodeId = gateNode.Id }
            }
        };
        return (quest, gateNode.Id);
    }

    private static QuestNodeExecutionContext CtxWithUpstreamOutput(
        QuestEntity quest, Guid gateId, QuestNode upstreamNode, string upstreamOutput)
    {
        var runId = Guid.NewGuid();
        var exec = new QuestNodeExecution
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            NodeId = upstreamNode.Id,
            State = QuestNodeState.Succeeded,
            Output = upstreamOutput
        };
        return new QuestNodeExecutionContext(runId, gateId, quest, quest.AvatarId,
            new Dictionary<Guid, QuestNodeExecution> { [upstreamNode.Id] = exec });
    }

    [Fact]
    public void NodeType_And_ChainCapability()
    {
        IQuestNodeHandler handler = new GateCheckNodeHandler(HolonManagerMocks.Empty());
        handler.NodeType.Should().Be(QuestNodeType.GateCheck);
        handler.RequiresChainCapability.Should().BeFalse();
    }

    [Fact]
    public async Task Pass_ReturnsOk_WithPassTrue()
    {
        var (quest, gateId) = BuildQuest("upstream.bal.amount > 100", null, out var upstream);
        var ctx = CtxWithUpstreamOutput(quest, gateId, upstream, "{\"amount\":150}");

        var handler = new GateCheckNodeHandler(HolonManagerMocks.Empty());
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        result.Output.Should().Be("{\"pass\":true}");
    }

    [Fact]
    public async Task Fail_ReturnsIsError_WithGateNotMetMessage()
    {
        var (quest, gateId) = BuildQuest("upstream.bal.amount > 100", null, out var upstream);
        var ctx = CtxWithUpstreamOutput(quest, gateId, upstream, "{\"amount\":50}");

        var handler = new GateCheckNodeHandler(HolonManagerMocks.Empty());
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("gate not met:");
    }

    [Fact]
    public async Task Reads_AreResolvedFromConfig()
    {
        var reads = new Dictionary<string, JsonElement>
        {
            ["kyc"] = JsonDocument.Parse("\"verified\"").RootElement.Clone()
        };
        var (quest, gateId) = BuildQuest("reads.kyc == 'verified'", reads, out var upstream);
        var ctx = CtxWithUpstreamOutput(quest, gateId, upstream, "{\"amount\":1}");

        var handler = new GateCheckNodeHandler(HolonManagerMocks.Empty());
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        result.Output.Should().Be("{\"pass\":true}");
    }

    [Fact]
    public async Task MalformedPredicate_ReturnsFail_NeverThrows()
    {
        var (quest, gateId) = BuildQuest("System.IO.File.ReadAllText('x')", null, out var upstream);
        var ctx = CtxWithUpstreamOutput(quest, gateId, upstream, "{\"amount\":1}");

        var handler = new GateCheckNodeHandler(HolonManagerMocks.Empty());
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("gate predicate error:");
    }

    [Fact]
    public async Task MissingUpstreamPath_FailsClosed()
    {
        var (quest, gateId) = BuildQuest("upstream.bal.nonexistent > 1", null, out var upstream);
        var ctx = CtxWithUpstreamOutput(quest, gateId, upstream, "{\"amount\":1}");

        var handler = new GateCheckNodeHandler(HolonManagerMocks.Empty());
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("gate predicate error:");
    }

    // ─────────────────── Holon-state predicate resolver (§8.1) ───────────────────

    private static (QuestEntity quest, Guid gateId) BuildHolonGateQuest(
        string predicate, Guid ownerAvatarId, params Guid[] holons)
    {
        var cfg = new GateCheckNodeConfig
        {
            Predicate = predicate,
            Holons = holons.ToList()
        };
        var gateNode = new QuestNode
        {
            Id = Guid.NewGuid(),
            Name = "gate",
            NodeType = QuestNodeType.GateCheck,
            Config = JsonSerializer.Serialize(cfg)
        };
        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            // The run owner: the GateCheck holon resolver is owner-scoped, so a holon
            // is only readable when its AvatarId matches this.
            AvatarId = ownerAvatarId,
            Nodes = new List<QuestNode> { gateNode },
            Edges = new List<QuestEdge>()
        };
        return (quest, gateNode.Id);
    }

    private static QuestNodeExecutionContext PlainCtx(QuestEntity quest, Guid gateId) =>
        new(Guid.NewGuid(), gateId, quest, quest.AvatarId, new Dictionary<Guid, QuestNodeExecution>());

    [Fact]
    public async Task HolonState_FieldMatches_Passes()
    {
        // holon.<id>.status reads the holon's CURRENT lifecycle state directly. The
        // holon is owned by the run owner, so the owner-scoped read succeeds.
        var owner = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var (quest, gateId) = BuildHolonGateQuest(
            $"holon.{projectId}.status == 'FUNDED'", owner, projectId);
        var holon = HolonManagerMocks.HolonWithStatus(projectId, "FUNDED", owner);

        var handler = new GateCheckNodeHandler(HolonManagerMocks.WithHolons(holon));
        var result = await handler.HandleAsync(PlainCtx(quest, gateId));

        result.IsError.Should().BeFalse(result.Message);
        result.Output.Should().Be("{\"pass\":true}");
    }

    [Fact]
    public async Task HolonState_FieldDoesNotMatch_Fails()
    {
        var owner = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var (quest, gateId) = BuildHolonGateQuest(
            $"holon.{projectId}.status == 'FUNDED'", owner, projectId);
        var holon = HolonManagerMocks.HolonWithStatus(projectId, "DRAFT", owner);

        var handler = new GateCheckNodeHandler(HolonManagerMocks.WithHolons(holon));
        var result = await handler.HandleAsync(PlainCtx(quest, gateId));

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("gate not met:");
    }

    [Fact]
    public async Task HolonState_MissingHolon_FailsClosed()
    {
        // A configured holon the manager cannot resolve fails the gate CLOSED —
        // never silently passes when the gated lifecycle state is unreadable.
        var projectId = Guid.NewGuid();
        var (quest, gateId) = BuildHolonGateQuest(
            $"holon.{projectId}.status == 'FUNDED'", Guid.NewGuid(), projectId);

        var handler = new GateCheckNodeHandler(HolonManagerMocks.Empty());
        var result = await handler.HandleAsync(PlainCtx(quest, gateId));

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("gate scope error:");
    }

    [Fact]
    public async Task HolonState_NotOwnedByRunOwner_FailsClosed()
    {
        // A holon owned by a DIFFERENT avatar than the run owner must NOT be readable
        // (cross-tenant data oracle). It fails the gate CLOSED with the same not-found
        // wording as a missing holon, so existence cannot be probed across tenants.
        var owner = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var (quest, gateId) = BuildHolonGateQuest(
            $"holon.{projectId}.status == 'FUNDED'", owner, projectId);
        // Holon exists and would satisfy the predicate — but it belongs to otherTenant.
        var holon = HolonManagerMocks.HolonWithStatus(projectId, "FUNDED", otherTenant);

        var handler = new GateCheckNodeHandler(HolonManagerMocks.WithHolons(holon));
        var result = await handler.HandleAsync(PlainCtx(quest, gateId));

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("gate scope error:");
        result.Message.Should().Contain("not found or unreadable");
    }

    [Fact]
    public async Task HolonState_TypedField_AndMetadata_BothReadable()
    {
        // The flattened holon state exposes BOTH a typed field (isActive) and a
        // metadata lifecycle field (phase) to the predicate in one gate.
        var owner = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var (quest, gateId) = BuildHolonGateQuest(
            $"holon.{projectId}.isActive == true && holon.{projectId}.phase == 'work'", owner, projectId);
        var holon = HolonManagerMocks.HolonWithStatus(projectId, "IN_PROGRESS", owner, ("phase", "work"));

        var handler = new GateCheckNodeHandler(HolonManagerMocks.WithHolons(holon));
        var result = await handler.HandleAsync(PlainCtx(quest, gateId));

        result.IsError.Should().BeFalse(result.Message);
        result.Output.Should().Be("{\"pass\":true}");
    }
}
