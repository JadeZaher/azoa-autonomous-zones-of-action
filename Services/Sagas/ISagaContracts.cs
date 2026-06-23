namespace AZOA.WebAPI.Sagas;

/// <summary>
/// A step handler does the actual work for one forward (or compensation) step.
/// It is the ONLY place a saga touches the outside world; it MUST gate any
/// irreversible effect on <c>ctx.StepIdempotencyKey</c> via the shared
/// <see cref="AZOA.WebAPI.Interfaces.IIdempotencyStore"/> (the saga layer does
/// not reinvent exactly-once). Generic over a typed payload — zero bridge
/// coupling.
/// </summary>
/// <typeparam name="TPayload">The JSON-serialized step payload type.</typeparam>
public interface IStepHandler<TPayload>
{
    Task<StepResult> ExecuteAsync(StepExecutionContext<TPayload> ctx, CancellationToken ct);
}

/// <summary>
/// Non-generic erased view of a step handler so the processor can dispatch
/// without knowing the payload type. The concrete <see cref="SagaStep{T}"/>
/// closes the generic and JSON-deserializes the persisted payload.
/// </summary>
public interface IStepDispatch
{
    Task<StepResult> DispatchAsync(
        string sagaName,
        string stepName,
        string correlationKey,
        string stepIdempotencyKey,
        int attempt,
        string payloadJson,
        IServiceProvider scope,
        CancellationToken ct);
}

/// <summary>
/// One declared step in a saga: its name, the handler type to resolve per
/// attempt, its retry policy, and (for a forward step) the OPTIONAL name of the
/// declared compensation step to run if it exhausts retries. Compensation is a
/// first-class step — it is itself an <see cref="ISagaStep"/> with its own
/// idempotency key, retry policy and dead-letter behaviour.
/// </summary>
public interface ISagaStep
{
    /// <summary>Unique step name within the owning saga definition.</summary>
    string Name { get; }

    /// <summary>Retry/backoff policy governing this step.</summary>
    RetryPolicy RetryPolicy { get; }

    /// <summary>
    /// For a forward step: the name of the declared compensation step to
    /// enqueue when this step exhausts its retry budget. <c>null</c> ⇒ no
    /// compensation declared (the step dead-letters on exhaustion). A
    /// compensation step itself carries <c>null</c> here (it dead-letters if it
    /// in turn exhausts).
    /// </summary>
    string? CompensationStepName { get; }

    /// <summary>Erased dispatcher that closes the generic payload type and
    /// resolves the handler from the per-tick DI scope.</summary>
    IStepDispatch Dispatch { get; }
}

/// <summary>
/// A registered saga: a named, ordered list of forward steps plus any
/// compensation steps, all resolvable by name. Adding a new saga = register one
/// of these + its handlers; NO core change. The bridge will be exactly one such
/// definition in a later phase — it is NOT special here.
/// </summary>
public interface ISagaDefinition
{
    /// <summary>Unique saga name (stored on every step record).</summary>
    string Name { get; }

    /// <summary>Forward steps in execution order.</summary>
    IReadOnlyList<ISagaStep> ForwardSteps { get; }

    /// <summary>Resolve any step (forward or compensation) by name, or
    /// <c>null</c> if this definition has no such step.</summary>
    ISagaStep? FindStep(string stepName);

    /// <summary>The forward step that follows <paramref name="stepName"/>, or
    /// <c>null</c> if it is the last forward step (⇒ saga completes).</summary>
    ISagaStep? NextForwardStep(string stepName);
}

/// <summary>
/// Resolves a registered <see cref="ISagaDefinition"/> by name. Implemented as a
/// simple immutable registry built from all DI-registered definitions — the
/// processor only ever asks "give me the definition for this record's saga
/// name", keeping it storage- and domain-agnostic.
/// </summary>
public interface ISagaRegistry
{
    ISagaDefinition? Find(string sagaName);
}
