using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Services.Quest;
using OASIS.WebAPI.Services.Quest.Handlers;
using Xunit;
using QuestEntity = OASIS.WebAPI.Models.Quest.Quest;

namespace OASIS.WebAPI.Tests.Quest.Handlers;

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
        return new QuestNodeExecutionContext(runId, gateId, quest,
            new Dictionary<Guid, QuestNodeExecution> { [upstreamNode.Id] = exec });
    }

    [Fact]
    public void NodeType_And_ChainCapability()
    {
        IQuestNodeHandler handler = new GateCheckNodeHandler();
        handler.NodeType.Should().Be(QuestNodeType.GateCheck);
        handler.RequiresChainCapability.Should().BeFalse();
    }

    [Fact]
    public async Task Pass_ReturnsOk_WithPassTrue()
    {
        var (quest, gateId) = BuildQuest("upstream.bal.amount > 100", null, out var upstream);
        var ctx = CtxWithUpstreamOutput(quest, gateId, upstream, "{\"amount\":150}");

        var handler = new GateCheckNodeHandler();
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        result.Output.Should().Be("{\"pass\":true}");
    }

    [Fact]
    public async Task Fail_ReturnsIsError_WithGateNotMetMessage()
    {
        var (quest, gateId) = BuildQuest("upstream.bal.amount > 100", null, out var upstream);
        var ctx = CtxWithUpstreamOutput(quest, gateId, upstream, "{\"amount\":50}");

        var handler = new GateCheckNodeHandler();
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

        var handler = new GateCheckNodeHandler();
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        result.Output.Should().Be("{\"pass\":true}");
    }

    [Fact]
    public async Task MalformedPredicate_ReturnsFail_NeverThrows()
    {
        var (quest, gateId) = BuildQuest("System.IO.File.ReadAllText('x')", null, out var upstream);
        var ctx = CtxWithUpstreamOutput(quest, gateId, upstream, "{\"amount\":1}");

        var handler = new GateCheckNodeHandler();
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("gate predicate error:");
    }

    [Fact]
    public async Task MissingUpstreamPath_FailsClosed()
    {
        var (quest, gateId) = BuildQuest("upstream.bal.nonexistent > 1", null, out var upstream);
        var ctx = CtxWithUpstreamOutput(quest, gateId, upstream, "{\"amount\":1}");

        var handler = new GateCheckNodeHandler();
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("gate predicate error:");
    }
}
