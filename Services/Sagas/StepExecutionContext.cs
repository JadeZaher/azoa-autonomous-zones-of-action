namespace AZOA.WebAPI.Sagas;

/// <summary>
/// What the processor hands a step handler for one attempt. Deliberately small
/// and storage-agnostic: a handler never sees EF, the outbox table, the trigger,
/// or any bridge type — it only sees its typed payload, the correlation key, and
/// the per-step idempotency key it MUST use to gate any irreversible effect.
///
/// <para><b>Idempotency contract.</b> Before performing any irreversible
/// (on-chain / external) effect a handler MUST claim
/// <see cref="StepIdempotencyKey"/> on the shared
/// <see cref="AZOA.WebAPI.Interfaces.IIdempotencyStore"/> (the
/// api-safety-hardening spine) and only proceed on <c>Won == true</c>; on
/// <c>Won == false</c> it replays the recorded outcome. The saga layer does NOT
/// reimplement exactly-once — it composes the existing primitive. The processor
/// guarantees this key is STABLE across crash/retry/reclaim of the same step so
/// a re-run is a genuine idempotent replay, never a second effect.</para>
/// </summary>
/// <typeparam name="TPayload">The step's typed, JSON-serialized payload.</typeparam>
public sealed class StepExecutionContext<TPayload>
{
    public StepExecutionContext(
        string sagaName,
        string stepName,
        string correlationKey,
        string stepIdempotencyKey,
        int attempt,
        TPayload payload)
    {
        SagaName = sagaName;
        StepName = stepName;
        CorrelationKey = correlationKey;
        StepIdempotencyKey = stepIdempotencyKey;
        Attempt = attempt;
        Payload = payload;
    }

    /// <summary>Registered saga definition name (e.g. "sample-counter").</summary>
    public string SagaName { get; }

    /// <summary>The current step's name within the definition.</summary>
    public string StepName { get; }

    /// <summary>
    /// Caller/business correlation key (reuses the api-safety-hardening key
    /// convention). Stable for the whole saga instance — ties every step record
    /// of one run together. NOT itself the idempotency key.
    /// </summary>
    public string CorrelationKey { get; }

    /// <summary>
    /// Stable per-step idempotency key the handler MUST use to gate any
    /// irreversible effect via <c>IIdempotencyStore</c>. Derived from the
    /// correlation key + step name so it is identical across every retry /
    /// reclaim of THIS step, but distinct from sibling steps and the
    /// compensation step (which carries its own key).
    /// </summary>
    public string StepIdempotencyKey { get; }

    /// <summary>1-based attempt number for this invocation.</summary>
    public int Attempt { get; }

    /// <summary>The deserialized step payload.</summary>
    public TPayload Payload { get; }
}

/// <summary>
/// A handler's request to SUSPEND its run at the current step instead of
/// completing or failing it (the durable-workflow-engine extension). The
/// processor parks the step: it becomes <see cref="StepStatus.Parked"/>,
/// invisible to the due-step claim scan until either a matching
/// <c>SignalAsync(correlationKey, <see cref="GateId"/>, …)</c> un-parks it
/// (gate node) or — when <see cref="ResumeAt"/> is set — its timer becomes due
/// (wait node). The park is itself an idempotent durable transition, so a
/// crashed processor leaves a resumable parked row, never a lost suspension.
/// </summary>
/// <param name="GateId">The signal key the parked step waits on. A gate node
/// supplies a stable id (e.g. "phase-met"); a pure timer/wait node may leave
/// this an empty string and rely on <see cref="ResumeAt"/>.</param>
/// <param name="ResumeAt">Optional forward UTC time at which the parked step
/// becomes due even without a signal (a wait/timer node). <c>null</c> ⇒ the
/// step parks indefinitely until signalled.</param>
public sealed record ParkRequest(string GateId, DateTime? ResumeAt);

/// <summary>
/// Outcome of one step-handler attempt. The processor maps this to a durable
/// state transition (advance / schedule retry / compensate / dead-letter /
/// PARK) exactly the way <c>ReconciliationService</c> maps a chain verdict — the
/// handler itself never touches the store.
/// </summary>
public sealed record StepResult(bool Success, string? Output, string? Error, ParkRequest? Park = null)
{
    /// <summary>The step's irreversible effect is done (or was an idempotent
    /// replay). <paramref name="output"/> is persisted on the record for
    /// observability and may seed the next step.</summary>
    public static StepResult Ok(string? output = null) => new(true, output, null);

    /// <summary>The attempt failed. The processor increments the attempt count
    /// and either schedules a backoff retry or, at the budget, routes to the
    /// declared compensation (forward) / dead-letters (compensation).</summary>
    public static StepResult Fail(string error) => new(false, null, error);

    /// <summary>
    /// SUSPEND the run at this step (durable-workflow-engine). The step parks on
    /// <paramref name="gateId"/> until a matching signal arrives; when
    /// <paramref name="resumeAt"/> is supplied the step also auto-resumes once
    /// that timer is due (a wait node). Distinct from <see cref="Ok"/> (which
    /// advances) and <see cref="Fail"/> (which retries/compensates): a parked
    /// step neither advances nor consumes a retry attempt.
    /// </summary>
    public static StepResult Parked(string gateId, DateTime? resumeAt = null) =>
        new(false, null, null, new ParkRequest(gateId, resumeAt));
}
