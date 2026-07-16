using Microsoft.Extensions.Options;
using AZOA.WebAPI.Models.Sagas;

namespace AZOA.WebAPI.Sagas;

/// <summary>
/// The step processor. One <see cref="ProcessDueStepsAsync"/> call:
/// <list type="number">
/// <item>asks the store for due step ids (the store also reclaims lapsed
/// leases first — crash-safe re-entry);</item>
/// <item>for each, atomically CLAIMS it (single-winner conditional UPDATE);</item>
/// <item>resolves its definition + step, runs the handler in the per-tick
/// scope;</item>
/// <item>on success: advances to the next forward step (transactional outbox
/// continuation) or completes the saga;</item>
/// <item>on failure: schedules a backoff retry; at the attempt budget routes a
/// forward step to its declared compensation step (itself a step with its own
/// idempotency key), or dead-letters (a forward step with no compensation, or a
/// compensation step that itself exhausted).</item>
/// </list>
/// Every transition is the store's conditional + assert-one-row primitive, so
/// it is idempotent and crash-safe: a process death between any two steps
/// leaves a durable record the next tick resumes. Handlers gate irreversible
/// effects on the stable per-step idempotency key, so a resumed/retried step is
/// an idempotent replay — never a double effect.
///
/// <para>Mirrors <c>ReconciliationService</c>'s shape: snapshot ids, per-record
/// try/catch, never throw out of the loop for one bad step.</para>
/// </summary>
public sealed class SagaProcessor : ISagaProcessor
{
    private readonly ISagaStore _store;
    private readonly ISagaRegistry _registry;
    private readonly IServiceProvider _scope;
    private readonly ILogger<SagaProcessor> _logger;
    private readonly SagaOptions _options;

    public SagaProcessor(
        ISagaStore store,
        ISagaRegistry registry,
        IServiceProvider scope,
        ILogger<SagaProcessor> logger,
        IOptions<SagaOptions> options)
    {
        _store = store;
        _registry = registry;
        _scope = scope;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<int> ProcessDueStepsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var batch = Math.Clamp(_options.BatchSize, 1, 1000);
        var lease = TimeSpan.FromSeconds(Math.Max(1, _options.LeaseTimeoutSeconds));

        var dueIds = await _store.GetDueStepIdsAsync(now, batch, lease, ct);
        var processed = 0;

        foreach (var id in dueIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await ProcessOneAsync(id, ct))
                    processed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A single step must never abort the tick. The step stays
                // InProgress and is reclaimed by the lease timeout — exactly the
                // crash-safe contract.
                _logger.LogError(ex,
                    "Saga processor: unexpected error processing step {StepId} — " +
                    "left for lease reclaim.", id);
            }
        }

        return processed;
    }

    private async Task<bool> ProcessOneAsync(Guid id, CancellationToken ct)
    {
        // Atomic single-winner claim. Lost the race / no longer due ⇒ skip.
        var claimedNow = DateTime.UtcNow;
        var step = await _store.TryClaimDueStepAsync(id, claimedNow, ct);
        if (step is null)
            return false;
        var claimedAt = step.ClaimedAt
            ?? throw new InvalidOperationException(
                $"Claimed saga step '{step.Id}' returned without a lease token.");

        var def = _registry.Find(step.SagaName);
        if (def is null)
        {
            _logger.LogError(
                "Saga processor: step {StepId} references unregistered saga " +
                "'{SagaName}' — dead-lettering.", step.Id, step.SagaName);
            await _store.DeadLetterStepAsync(step.Id, claimedAt,
                $"No saga definition registered for '{step.SagaName}'.", ct);
            return true;
        }

        var stepDef = def.FindStep(step.StepName);
        if (stepDef is null)
        {
            _logger.LogError(
                "Saga processor: saga '{SagaName}' has no step '{StepName}' " +
                "(step {StepId}) — dead-lettering.",
                step.SagaName, step.StepName, step.Id);
            await _store.DeadLetterStepAsync(step.Id, claimedAt,
                $"Saga '{step.SagaName}' has no step '{step.StepName}'.", ct);
            return true;
        }

        var attempt = step.AttemptCount + 1;
        StepResult result;
        try
        {
            result = await stepDef.Dispatch.DispatchAsync(
                step.SagaName, step.StepName, step.CorrelationKey,
                step.StepIdempotencyKey, attempt, step.Payload, _scope, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Handler threw — treat as a failed attempt (durably recorded).
            result = StepResult.Fail($"Handler threw: {ex.GetType().Name}: {ex.Message}");
        }

        // A handler may request SUSPENSION instead of completing or failing
        // (durable-workflow-engine): park the step on its gate/timer. A parked
        // step neither advances the saga nor consumes a retry attempt — it waits
        // for SignalAsync or its timer. Checked before the success/fail fork so
        // a park is never misread as a failed attempt.
        if (result.Park is { } park)
        {
            await OnStepParkedAsync(step, claimedAt, park, ct);
            return true;
        }

        if (result.Success)
        {
            await OnStepSucceededAsync(step, claimedAt, def, result.Output, ct);
            return true;
        }

        await OnStepFailedAsync(
            step, claimedAt, stepDef, attempt, result.Error ?? "unknown error", ct);
        return true;
    }

    private async Task OnStepParkedAsync(
        SagaStepRecord step, DateTime claimedAt, ParkRequest park, CancellationToken ct)
    {
        var parked = await _store.ParkStepAsync(
            step.Id, claimedAt, park.GateId, park.ResumeAt, ct);
        if (!parked)
        {
            // A concurrent reclaim already transitioned this row out of
            // InProgress — idempotent no-op (the winner drives the outcome).
            _logger.LogInformation(
                "Saga processor: step {StepId} ({Saga}/{Step}) no longer InProgress " +
                "at park (concurrent reclaim) — no-op.",
                step.Id, step.SagaName, step.StepName);
            return;
        }

        if (park.ResumeAt is { } resumeAt)
            _logger.LogInformation(
                "Saga processor: step {StepId} ({Saga}/{Step}) PARKED on gate " +
                "'{Gate}' with timer due {ResumeAt:O} — resumes on signal or timer.",
                step.Id, step.SagaName, step.StepName, park.GateId, resumeAt);
        else
            _logger.LogInformation(
                "Saga processor: step {StepId} ({Saga}/{Step}) PARKED on gate " +
                "'{Gate}' — suspended until signalled.",
                step.Id, step.SagaName, step.StepName, park.GateId);
    }

    private async Task OnStepSucceededAsync(
        SagaStepRecord step, DateTime claimedAt, ISagaDefinition def,
        string? output, CancellationToken ct)
    {
        var next = step.IsCompensation ? null : def.NextForwardStep(step.StepName);
        if (step.IsCompensation || next is null)
        {
            var completed = await _store.CompleteStepAsync(step.Id, claimedAt, output, ct);
            if (!completed)
            {
                _logger.LogInformation(
                    "Saga processor: step {StepId} ({Saga}/{Step}) lost lease " +
                    "at completion — no-op.",
                    step.Id, step.SagaName, step.StepName);
                return;
            }

            if (step.IsCompensation)
            {
                _logger.LogInformation(
                    "Saga processor: compensation step {StepId} ({Saga}/{Step}) completed " +
                    "— saga instance '{Correlation}' compensated.",
                    step.Id, step.SagaName, step.StepName, step.CorrelationKey);
            }
            else
            {
                _logger.LogInformation(
                    "Saga processor: final forward step {StepId} ({Saga}/{Step}) completed " +
                    "— saga instance '{Correlation}' COMPLETED.",
                    step.Id, step.SagaName, step.StepName, step.CorrelationKey);
            }
            return;
        }

        var nextIdemKey = SagaKeys.StepIdempotencyKey(step.CorrelationKey, next.Name);
        var successor = await _store.CompleteAndEnqueueNextStepAsync(
            step.Id,
            claimedAt,
            output,
            step.SagaName,
            next.Name,
            step.CorrelationKey,
            nextIdemKey,
            // The saga-instance payload flows UNCHANGED through forward steps —
            // a generic module must not assume a step's output is the next
            // step's input (that coupling is a consumer concern). The completed
            // step's output is recorded on its own record for observability.
            step.Payload, ct);
        if (successor is null)
        {
            _logger.LogInformation(
                "Saga processor: step {StepId} ({Saga}/{Step}) lost lease " +
                "before atomic continuation — no-op.",
                step.Id, step.SagaName, step.StepName);
            return;
        }

        _logger.LogInformation(
            "Saga processor: step {StepId} ({Saga}/{Step}) completed — enqueued next " +
            "forward step '{Next}'.", step.Id, step.SagaName, step.StepName, next.Name);
    }

    private async Task OnStepFailedAsync(
        SagaStepRecord step, DateTime claimedAt, ISagaStep stepDef, int attempt, string error,
        CancellationToken ct)
    {
        var policy = stepDef.RetryPolicy;

        if (!policy.IsExhausted(attempt))
        {
            var delay = policy.NextDelay(attempt);
            var nextRunAt = DateTime.UtcNow + delay;
            var ok = await _store.ScheduleRetryAsync(
                step.Id, claimedAt, nextRunAt, error, ct);
            if (ok)
                _logger.LogWarning(
                    "Saga processor: step {StepId} ({Saga}/{Step}) attempt {Attempt} " +
                    "failed ({Error}) — retry scheduled at {NextRunAt:O} (backoff {Delay}).",
                    step.Id, step.SagaName, step.StepName, attempt, error, nextRunAt, delay);
            return;
        }

        // Attempt budget exhausted.
        if (!step.IsCompensation && stepDef.CompensationStepName is { } compName)
        {
            var compIdemKey = SagaKeys.StepIdempotencyKey(step.CorrelationKey, compName);
            var comp = await _store.CompensateStepAsync(
                step.Id, claimedAt, compName, compIdemKey, step.Payload, error, ct);
            if (comp is not null)
                _logger.LogError(
                    "Saga processor: forward step {StepId} ({Saga}/{Step}) exhausted " +
                    "{Max} attempts ({Error}) — routed to compensation step '{Comp}' " +
                    "(idempotency key '{Key}').",
                    step.Id, step.SagaName, step.StepName, policy.MaxAttempts, error,
                    compName, compIdemKey);
            return;
        }

        // No declared compensation (or a compensation step that itself
        // exhausted) ⇒ dead-letter for an operator.
        var dl = await _store.DeadLetterStepAsync(step.Id, claimedAt, error, ct);
        if (dl)
            _logger.LogError(
                "Saga processor: step {StepId} ({Saga}/{Step}, compensation={IsComp}) " +
                "exhausted {Max} attempts ({Error}) with no further recourse — " +
                "DEAD-LETTERED. MANUAL INTERVENTION required.",
                step.Id, step.SagaName, step.StepName, step.IsCompensation,
                policy.MaxAttempts, error);
    }
}
