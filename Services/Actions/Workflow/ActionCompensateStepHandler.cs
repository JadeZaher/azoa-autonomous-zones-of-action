using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Sagas;

namespace AZOA.WebAPI.Services.Actions.Workflow;

/// <summary>Closes proven effect-free failures and quarantines effects requiring domain compensation.</summary>
public sealed class ActionCompensateStepHandler(IActionRunStore runStore, IActionNodeExecutionStore executionStore,
    ILogger<ActionCompensateStepHandler> logger) : IStepHandler<ActionCompensatePayload>
{
    /// <inheritdoc/>
    public async Task<StepResult> ExecuteAsync(StepExecutionContext<ActionCompensatePayload> ctx, CancellationToken ct)
    {
        var loaded = await runStore.GetByIdAsync(ctx.Payload.RunId, ct);
        if (loaded.IsError || loaded.Result is null) return StepResult.Fail("Compensation run is unavailable.");
        var run = loaded.Result;
        if (run.Status != ActionRunStatus.AwaitingReconciliation)
            return StepResult.Parked("operator:compensation:" + run.Id.ToString("N"));
        var executions = await executionStore.GetByRunIdAsync(run.Id, ct);
        if (executions.IsError || executions.Result is null)
            return StepResult.Parked("operator:compensation:" + run.Id.ToString("N"));
        var unresolved = executions.Result.Any(execution => execution.State == ActionNodeState.Running
            || execution.State == ActionNodeState.Succeeded && !ProvenRead(execution)
            || execution.State == ActionNodeState.Failed && !ProvenRead(execution));
        if (unresolved)
        {
            logger.LogWarning("Action run {RunId} requires domain compensation evidence; no reversal was performed.", run.Id);
            return StepResult.Parked("operator:compensation:" + run.Id.ToString("N"));
        }
        var expected = run.Status;
        run.Status = ActionRunStatus.Failed;
        run.EndedAt = DateTime.UtcNow;
        var closed = await runStore.UpdateAsync(run, expected, ct);
        return closed.IsError ? StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(2))
            : StepResult.Ok("Workflow failed without a completed effect requiring reversal.");
    }

    private static bool ProvenRead(ActionNodeExecution execution)
    {
        if (execution.FrozenInput is null || execution.ExternalOperation is not { Domain: "read" } reference) return false;
        var input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(execution.FrozenInput);
        return input is { Version: 1 } && reference.Version == 1 && ActionReadRecovery.Supports(input.NodeType)
            && input.RunId == execution.RunId && input.NodeId == execution.NodeId
            && reference.IntentHash == CanonicalJson.HashCanonical(execution.FrozenInput);
    }
}
