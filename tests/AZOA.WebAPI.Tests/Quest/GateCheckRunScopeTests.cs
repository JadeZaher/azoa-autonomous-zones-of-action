using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Handlers;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// GateCheck predicates may reference <c>run.&lt;nodeName&gt;.&lt;field&gt;</c> — the
/// run-scoped root the shared grammar (<c>GatePath.ValidRoots</c>) already accepts.
/// Regression guard for the asymmetry where <c>run.</c> worked in $from bindings but
/// failed closed with "unknown path" in gate predicates. Scope is built from
/// <c>context.AllRunExecutions</c>, mirroring <c>QuestConfigBindingResolver.BuildRunScope</c>.
/// See Services/Quest/AGENTS.md §gate-predicate.
/// </summary>
public class GateCheckRunScopeTests
{
    // A quest with a prior node "mint" and a gate node with NO direct edge from mint,
    // so the reference can ONLY resolve via the run-scoped root (not upstream.).
    private static (QuestEntity quest, Guid gateId, Guid priorNodeId) BuildRunScopeQuest(string predicate)
    {
        var priorNode = new QuestNode { Id = Guid.NewGuid(), Name = "mint" };
        var gateNode = new QuestNode
        {
            Id = Guid.NewGuid(),
            Name = "gate",
            NodeType = QuestNodeType.GateCheck,
            Config = JsonSerializer.Serialize(new GateCheckNodeConfig { Predicate = predicate })
        };
        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            AvatarId = Guid.NewGuid(),
            Nodes = new List<QuestNode> { priorNode, gateNode },
            // No edge from mint → gate: run. must resolve without a direct edge.
            Edges = new List<QuestEdge>()
        };
        return (quest, gateNode.Id, priorNode.Id);
    }

    private static QuestNodeExecutionContext CtxWithRunExecution(
        QuestEntity quest, Guid gateId, Guid priorNodeId, string? priorOutput)
    {
        var runId = Guid.NewGuid();
        var allRun = new Dictionary<Guid, QuestNodeExecution>();
        if (priorOutput is not null)
        {
            allRun[priorNodeId] = new QuestNodeExecution
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                NodeId = priorNodeId,
                State = QuestNodeState.Succeeded,
                Output = priorOutput
            };
        }
        return new QuestNodeExecutionContext(
            runId, gateId, quest, quest.AvatarId,
            upstreamExecutions: null, actingTenantId: null, allRunExecutions: allRun);
    }

    [Fact]
    public async Task RunScope_PredicateSatisfied_Passes()
    {
        // run.mint.Amount > 100, and mint emitted Amount=150 → gate passes.
        var (quest, gateId, priorId) = BuildRunScopeQuest("run.mint.Amount > 100");
        var ctx = CtxWithRunExecution(quest, gateId, priorId, "{\"Amount\":150}");

        var handler = new GateCheckNodeHandler(HolonManagerMocks.Empty());
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse(result.Message);
        result.Output.Should().Be("{\"pass\":true}");
    }

    [Fact]
    public async Task RunScope_PredicateNotSatisfied_FailsGate_NotUnknownPath()
    {
        // Same predicate, mint emitted Amount=50 → gate NOT met. The path resolved
        // (no "unknown path"); the gate outcome is simply false.
        var (quest, gateId, priorId) = BuildRunScopeQuest("run.mint.Amount > 100");
        var ctx = CtxWithRunExecution(quest, gateId, priorId, "{\"Amount\":50}");

        var handler = new GateCheckNodeHandler(HolonManagerMocks.Empty());
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("gate not met:");
        // The bug being guarded: a resolvable run. path must NOT surface as a
        // predicate/unknown-path error.
        result.Message.Should().NotContain("gate predicate error");
    }

    [Fact]
    public async Task RunScope_NoExecutionYet_FailsClosed()
    {
        // run.mint referenced but mint has no execution in the run → no scope key →
        // evaluator throws unknown-path → gate fails CLOSED (same posture as a
        // missing upstream. path).
        var (quest, gateId, priorId) = BuildRunScopeQuest("run.mint.Amount > 100");
        var ctx = CtxWithRunExecution(quest, gateId, priorId, priorOutput: null);

        var handler = new GateCheckNodeHandler(HolonManagerMocks.Empty());
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("gate predicate error:");
    }

    [Fact]
    public async Task RunScope_MissingMember_FailsClosed()
    {
        // mint executed but its output lacks the referenced field → member-not-found
        // → gate fails CLOSED, never silently false.
        var (quest, gateId, priorId) = BuildRunScopeQuest("run.mint.Amount > 100");
        var ctx = CtxWithRunExecution(quest, gateId, priorId, "{\"Other\":1}");

        var handler = new GateCheckNodeHandler(HolonManagerMocks.Empty());
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("gate predicate error:");
    }
}
