using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Sagas;

namespace AZOA.WebAPI.Services.Quest.Workflow;

/// <summary>
/// The per-RUN compensation handler for a durable quest run
/// (durable-workflow-engine §5, absorbs REVIEW Part-A M2). When a forward node
/// step exhausts its retry budget, the saga routes to this declared compensation
/// (<see cref="QuestWorkflowSaga.CompensateStepName"/>) via the first-class
/// <c>CompensateStepAsync</c> path.
///
/// <para>Because the DAG is dynamic, compensation is computed from the RUN'S
/// executed-node history rather than a per-node static name: the handler walks
/// the run's <see cref="QuestNodeExecution"/> rows and settles the run back. The
/// concrete value-reversing actions (refund a swap, claw back a grant) are the
/// <c>economic-primitive-nodes</c> track's node handlers; this handler owns the
/// MECHANISM — marking the run <see cref="QuestRunStatus.Cancelled"/> and giving
/// the reversal a single, idempotent, durable home keyed on the saga step
/// idempotency key.</para>
///
/// <para>Today (engine-only, before the economic node track lands) the
/// compensation records the settlement and projects the run to
/// <see cref="QuestRunStatus.Cancelled"/>. The hook for per-node reversal is the
/// executed-node walk below — value reversal plugs in there.</para>
/// </summary>
public sealed class QuestCompensateStepHandler : IStepHandler<QuestCompensatePayload>
{
    private readonly IQuestRunStore _runStore;
    private readonly IQuestNodeExecutionStore _executionStore;
    private readonly ILogger<QuestCompensateStepHandler> _logger;

    public QuestCompensateStepHandler(
        IQuestRunStore runStore,
        IQuestNodeExecutionStore executionStore,
        ILogger<QuestCompensateStepHandler> logger)
    {
        _runStore = runStore;
        _executionStore = executionStore;
        _logger = logger;
    }

    public async Task<StepResult> ExecuteAsync(
        StepExecutionContext<QuestCompensatePayload> ctx, CancellationToken ct)
    {
        var p = ctx.Payload;

        // Walk the run's executed nodes newest-first — the order value-reversing
        // actions would unwind in. The reversal actions themselves are the
        // economic-primitive-nodes track; the mechanism (durable, idempotent,
        // keyed on ctx.StepIdempotencyKey) is here.
        var execs = await _executionStore.GetByRunIdAsync(p.RunId, ct);
        var succeeded = (execs.Result ?? Enumerable.Empty<QuestNodeExecution>())
            .Where(e => e.State == QuestNodeState.Succeeded)
            .OrderByDescending(e => e.EndedAt ?? e.StartedAt)
            .ToList();

        _logger.LogInformation(
            "Quest workflow: compensating run {RunId} — {Count} succeeded node(s) to settle " +
            "(idempotency key '{Key}').", p.RunId, succeeded.Count, ctx.StepIdempotencyKey);

        // Settle the run: project Cancelled. A compensation step succeeding
        // settles the saga as compensated (SagaProcessor.OnStepSucceededAsync),
        // so this is the terminal projection for the cancelled branch.
        // Settle the run Cancelled — conditional on the current (non-terminal)
        // status so a concurrent settler/projector can't clobber it and a
        // terminal run is never regressed (Fix 3 conditional UpdateAsync).
        var runResult = await _runStore.GetByIdAsync(p.RunId, ct);
        if (runResult.Result is { } run && !run.Status.IsTerminal())
        {
            var expected = run.Status;
            run.Status = QuestRunStatus.Cancelled;
            run.EndedAt = DateTime.UtcNow;
            await _runStore.UpdateAsync(run, expectedStatus: expected, ct);
        }

        return StepResult.Ok($"compensated run {p.RunId}; settled {succeeded.Count} node(s)");
    }
}
