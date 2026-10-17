using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Workflow;
using AZOA.WebAPI.Tests.Fakes;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AZOA.WebAPI.Tests.Actions.Workflow;

public sealed class ActionRecoveryWaitTests
{
    [Fact]
    public async Task ElapsedDurableTimerExecutesWithoutAnExternalSignal()
    {
        var fixture = await Fixture.CreateAsync(ActionNodeState.Pending, DateTime.UtcNow.AddMinutes(-1));
        var outcome = await fixture.Handler.ExecuteAsync(fixture.Context, default);
        outcome.Success.Should().BeTrue();
        fixture.NodeHandler.Calls.Should().Be(1);
        var saved = (await fixture.Executions.GetByRunAndNodeAsync(fixture.Run.Id, fixture.Node.Id)).Result!;
        saved.State.Should().Be(ActionNodeState.Succeeded);
        saved.WaitUntil.Should().Be(fixture.Deadline);
    }

    [Fact]
    public async Task TimerReplaysPreserveTheFirstDeadline()
    {
        var fixture = await Fixture.CreateAsync(ActionNodeState.Pending, null);
        var first = await fixture.Handler.ExecuteAsync(fixture.Context, default);
        first.Park.Should().NotBeNull();
        var second = await fixture.Handler.ExecuteAsync(fixture.Context, default);
        second.Park!.ResumeAt.Should().Be(first.Park!.ResumeAt);
        fixture.NodeHandler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RunningLegacyEffectRequiresEvidenceInsteadOfRearmingItsTimer()
    {
        var fixture = await Fixture.CreateAsync(ActionNodeState.Running, null);
        var outcome = await fixture.Handler.ExecuteAsync(fixture.Context, default);
        outcome.Success.Should().BeFalse();
        outcome.Park!.GateId.Should().StartWith("operator:");
        outcome.Park.ResumeAt.Should().BeNull();
        fixture.NodeHandler.Calls.Should().Be(0);
    }

    private sealed record Fixture(ActionNodeStepHandler Handler, Counter NodeHandler,
        InMemoryActionNodeExecutionStore Executions, ActionRun Run, ActionNode Node,
        StepExecutionContext<ActionStepPayload> Context, DateTime? Deadline)
    {
        public static async Task<Fixture> CreateAsync(ActionNodeState state, DateTime? deadline)
        {
            var actor = Guid.NewGuid();
            var action = new ActionDefinition { Id = Guid.NewGuid(), UserId = actor };
            var node = new ActionNode
            {
                Id = Guid.NewGuid(), ActionId = action.Id, NodeType = ActionNodeType.Condition,
                Config = "{\"_workflow\":{\"advance\":\"timer\",\"resumeInSeconds\":3600}}",
                IsEntry = true, IsTerminal = true
            };
            action.Nodes.Add(node);
            var run = new ActionRun { Id = Guid.NewGuid(), UserId = actor, ActionId = action.Id, Status = ActionRunStatus.Running };
            ActionRunSnapshot.Capture(run, action);
            var runs = new InMemoryActionRunStore();
            await runs.CreateAsync(run);
            var executions = new InMemoryActionNodeExecutionStore();
            await executions.CreateAsync(new ActionNodeExecution
            {
                Id = Guid.NewGuid(), RunId = run.Id, NodeId = node.Id, State = state, WaitUntil = deadline
            });
            var counter = new Counter();
            var handler = new ActionNodeStepHandler(new InMemoryActionStore(), runs, executions,
                new ActionNodeHandlerRegistry([counter]), new InMemorySagaStore(), WalletManagerMocks.Empty(),
                BlockchainProviderFactoryFakes.Returning(), BindingResolverFakes.PassThrough(),
                NullLogger<ActionNodeStepHandler>.Instance, workflowAdmission: new RecordingActionWorkflowAdmissionStore());
            var context = new StepExecutionContext<ActionStepPayload>(ActionWorkflowSaga.Name, node.Id.ToString("N"),
                run.Id.ToString("N"), "stable-test-step", 1, new(run.Id, action.Id, actor, node.Id));
            return new(handler, counter, executions, run, node, context, deadline);
        }
    }

    private sealed class Counter : IActionNodeHandler
    {
        public ActionNodeType NodeType => ActionNodeType.Condition;
        public int Calls { get; private set; }
        public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(ActionNodeHandlerResult.Ok("{}"));
        }
    }
}
