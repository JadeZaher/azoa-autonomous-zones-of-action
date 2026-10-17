using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions.Workflow;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Actions.Workflow;

public sealed class ActionCompensationEvidenceTests
{
    [Theory]
    [InlineData(ActionNodeState.Succeeded)]
    [InlineData(ActionNodeState.Running)]
    public async Task CompletedOrUnresolvedEffectCannotBeReportedCompensated(ActionNodeState state)
    {
        var run = new ActionRun { Id = Guid.NewGuid(), Status = ActionRunStatus.AwaitingReconciliation };
        var runs = new Mock<IActionRunStore>();
        runs.Setup(store => store.GetByIdAsync(run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<ActionRun>.Success(run));
        var executions = new Mock<IActionNodeExecutionStore>();
        executions.Setup(store => store.GetByRunIdAsync(run.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<ActionNodeExecution>>.Success([new() { RunId = run.Id, State = state }]));
        var handler = new ActionCompensateStepHandler(runs.Object, executions.Object, NullLogger<ActionCompensateStepHandler>.Instance);
        var result = await handler.ExecuteAsync(new StepExecutionContext<ActionCompensatePayload>(ActionWorkflowSaga.Name,
            ActionWorkflowSaga.CompensateStepName, run.Id.ToString("N"), "compensation-key", 1,
            new(run.Id, Guid.NewGuid(), Guid.NewGuid())), default);
        result.Success.Should().BeFalse();
        result.Park!.GateId.Should().StartWith("operator:compensation:");
        runs.Verify(store => store.UpdateAsync(It.IsAny<ActionRun>(), It.IsAny<ActionRunStatus?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
