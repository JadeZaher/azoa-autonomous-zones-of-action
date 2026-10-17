using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services.Actions.Workflow;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using System.Text.Json;
using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Tests.Actions.Workflow;

public sealed class ActionDagSchedulerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OccurrenceBoundaryWaitsForSelectedParallelWorkAndIgnoresUnselectedBranch(bool handledFailure)
    {
        var fixture = await Fixture.Create(4, (0, 1));
        fixture.Graph.Edges.Add(new ActionEdge { SourceNodeId = fixture.Nodes[0].Id,
            TargetNodeId = fixture.Nodes[2].Id, EdgeType = ActionEdgeType.OnFailure });
        var run = (await fixture.Runs.GetByIdAsync(fixture.Run.Id)).Result!;
        run.OccurrenceBoundariesJson = JsonSerializer.Serialize(new[] { new DeploymentOccurrenceBoundary(fixture.Nodes[3].Id,
            fixture.Nodes.Take(3).Select(node => node.Id).ToArray()) });
        // Seed the immutable runtime metadata before scheduling in this in-memory fixture.
        var replacement = new Fixture { Graph = fixture.Graph, Nodes = fixture.Nodes, Run = run };
        await replacement.Runs.CreateAsync(run);
        foreach (var node in fixture.Nodes) await replacement.Executions.CreateAsync(new ActionNodeExecution
            { Id = Guid.NewGuid(), RunId = run.Id, NodeId = node.Id });
        fixture = replacement;
        await fixture.State(0, handledFailure ? ActionNodeState.Failed : ActionNodeState.Succeeded);
        await fixture.State(handledFailure ? 2 : 1, ActionNodeState.Running);
        await fixture.Advance();
        fixture.Admission.Nodes.Should().NotContain(fixture.Nodes[3].Id);
        await fixture.State(handledFailure ? 2 : 1, ActionNodeState.Succeeded);
        await fixture.Advance();
        (await fixture.Execution(handledFailure ? 1 : 2)).State.Should().Be(ActionNodeState.Skipped);
        fixture.Admission.Nodes.Should().Contain(fixture.Nodes[3].Id);
    }

    [Fact]
    public async Task UnevenDiamondWaitsForBothBranchesAndNeverCompletesAtFirstLeaf()
    {
        var fixture = await Fixture.Create(4, (0, 1), (0, 2), (1, 3), (2, 3));
        await fixture.State(0, ActionNodeState.Succeeded);
        await fixture.Advance();
        fixture.Admission.Nodes.Should().BeEquivalentTo([fixture.Nodes[1].Id, fixture.Nodes[2].Id]);
        await fixture.State(1, ActionNodeState.Succeeded);
        (await fixture.Advance()).Status.Should().Be(ActionRunStatus.Running);
        fixture.Admission.Nodes.Should().NotContain(fixture.Nodes[3].Id);
        await fixture.State(2, ActionNodeState.Succeeded);
        await Task.WhenAll(fixture.Advance(), fixture.Advance());
        fixture.Admission.Nodes.Should().ContainSingle(id => id == fixture.Nodes[3].Id);
        await fixture.State(3, ActionNodeState.Succeeded);
        (await fixture.Advance()).Status.Should().Be(ActionRunStatus.Succeeded);
    }

    [Fact]
    public async Task HandledFailureActivatesRecoveryAndPropagatesSkippedOrdinaryBranch()
    {
        var fixture = await Fixture.Create(4, (0, 1), (1, 3), (2, 3));
        fixture.Graph.Edges.Add(new ActionEdge { SourceNodeId = fixture.Nodes[0].Id,
            TargetNodeId = fixture.Nodes[2].Id, EdgeType = ActionEdgeType.OnFailure });
        await fixture.State(0, ActionNodeState.Failed);
        (await fixture.Advance()).UnhandledFailure.Should().BeFalse();
        (await fixture.Execution(1)).State.Should().Be(ActionNodeState.Skipped);
        fixture.Admission.Nodes.Should().Contain(fixture.Nodes[2].Id);
        await fixture.State(2, ActionNodeState.Succeeded);
        (await fixture.Advance()).Status.Should().Be(ActionRunStatus.Succeeded);
        (await fixture.Execution(3)).State.Should().Be(ActionNodeState.Skipped);
    }

    [Fact]
    public async Task UnhandledFailureRequestsCompensationWithoutSchedulingFreshEffects()
    {
        var fixture = await Fixture.Create(2, (0, 1));
        await fixture.State(0, ActionNodeState.Failed);
        var progress = await fixture.Advance();
        progress.UnhandledFailure.Should().BeTrue();
        progress.Status.Should().NotBe(ActionRunStatus.Succeeded);
        fixture.Admission.Nodes.Should().BeEmpty();
    }

    [Fact]
    public async Task SkippedRecoveryArmCannotTurnOriginalFailureIntoSuccess()
    {
        var fixture = await Fixture.Create(3, (1, 2));
        fixture.Graph.Edges.Add(new ActionEdge { SourceNodeId = fixture.Nodes[0].Id,
            TargetNodeId = fixture.Nodes[2].Id, EdgeType = ActionEdgeType.OnFailure });
        await fixture.State(0, ActionNodeState.Failed);
        await fixture.State(1, ActionNodeState.Failed);
        var progress = await fixture.Advance();
        (await fixture.Execution(2)).State.Should().Be(ActionNodeState.Skipped);
        progress.UnhandledFailure.Should().BeTrue();
        progress.Status.Should().NotBe(ActionRunStatus.Succeeded);
    }

    [Fact]
    public async Task FailureWaitsForAlreadyAdmittedSiblingBeforeCompensation()
    {
        var fixture = await Fixture.Create(3, (0, 1), (0, 2));
        await fixture.State(0, ActionNodeState.Succeeded);
        await fixture.State(1, ActionNodeState.Failed);
        await fixture.State(2, ActionNodeState.Running);
        var waiting = await fixture.Advance();
        waiting.UnhandledFailure.Should().BeTrue();
        waiting.HasAdmittedEffects.Should().BeTrue();
        waiting.Status.Should().Be(ActionRunStatus.AwaitingReconciliation);
        await fixture.State(2, ActionNodeState.Succeeded);
        var settled = await fixture.Advance();
        settled.UnhandledFailure.Should().BeTrue();
        settled.HasAdmittedEffects.Should().BeFalse();
    }

    [Fact]
    public async Task ReleasedManualGateSurvivesReplayAndAllowsAllSuccessors()
    {
        var fixture = await Fixture.Create(3, (0, 1), (0, 2));
        fixture.Nodes[0].Config = "{\"_workflow\":{\"advance\":\"manual\"}}";
        await fixture.State(0, ActionNodeState.Succeeded);
        (await fixture.Advance()).Status.Should().Be(ActionRunStatus.Suspended);
        fixture.Admission.Nodes.Should().BeEmpty();
        var manual = await fixture.Execution(0);
        manual.ManualAdvanceReleased = true;
        (await fixture.Executions.UpdateAsync(manual, ActionNodeState.Succeeded)).IsError.Should().BeFalse();
        await Task.WhenAll(fixture.Advance(), fixture.Advance());
        fixture.Admission.Nodes.Should().BeEquivalentTo([fixture.Nodes[1].Id, fixture.Nodes[2].Id]);
        (await fixture.Execution(0)).ManualAdvanceReleased.Should().BeTrue();
    }

    private sealed class Fixture
    {
        public required ActionDefinition Graph { get; init; }
        public required ActionRun Run { get; init; }
        public required ActionNode[] Nodes { get; init; }
        public InMemoryActionRunStore Runs { get; } = new();
        public InMemoryActionNodeExecutionStore Executions { get; } = new();
        public RecordingActionWorkflowAdmissionStore Admission { get; } = new();

        public static async Task<Fixture> Create(int count, params (int From, int To)[] edges)
        {
            var nodes = Enumerable.Range(0, count).Select(index => new ActionNode { Id = Guid.NewGuid(),
                Name = "node" + index, NodeType = ActionNodeType.Condition, Config = "{}", ExecutionOrder = index }).ToArray();
            var graph = new ActionDefinition { Id = Guid.NewGuid(), Nodes = nodes.ToList(), Edges = edges.Select(edge =>
                new ActionEdge { SourceNodeId = nodes[edge.From].Id, TargetNodeId = nodes[edge.To].Id,
                    EdgeType = ActionEdgeType.Control }).ToList() };
            var fixture = new Fixture { Graph = graph, Nodes = nodes,
                Run = new ActionRun { Id = Guid.NewGuid(), ActionId = graph.Id, UserId = Guid.NewGuid(), Status = ActionRunStatus.Running } };
            await fixture.Runs.CreateAsync(fixture.Run);
            foreach (var node in nodes) await fixture.Executions.CreateAsync(new ActionNodeExecution
                { Id = Guid.NewGuid(), NodeId = node.Id, RunId = fixture.Run.Id });
            return fixture;
        }

        public Task<ActionDagProgress> Advance() => new ActionDagScheduler(Runs, Executions, Admission).AdvanceAsync(Graph, Run.Id);
        public async Task<ActionNodeExecution> Execution(int index)
            => (await Executions.GetByRunAndNodeAsync(Run.Id, Nodes[index].Id)).Result!;
        public async Task State(int index, ActionNodeState state)
        {
            var row = await Execution(index);
            row.State = state;
            (await Executions.UpdateAsync(row)).IsError.Should().BeFalse();
        }
    }
}
