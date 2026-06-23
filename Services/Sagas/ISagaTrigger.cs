namespace AZOA.WebAPI.Sagas;

/// <summary>
/// The trigger seam. The processor never decides WHEN to look for work — a
/// trigger does, by invoking <paramref name="onTick"/>. The polling
/// implementation (<see cref="PollingSagaTrigger"/>) calls it on a timer; a
/// SurrealDB LIVE-query implementation will call it on a change-feed
/// notification instead — with ZERO change to the processor or any handler
/// (spec: trigger swappable poll → LIVE query). The processor only ever asks
/// the store "what steps are due?", which is trigger-agnostic.
/// </summary>
public interface ISagaTrigger
{
    /// <summary>
    /// Run the trigger loop until <paramref name="stoppingToken"/> fires,
    /// invoking <paramref name="onTick"/> once per trigger event. The callback
    /// MUST NOT throw (the polling impl additionally guards this); the trigger
    /// must never tear down the host.
    /// </summary>
    Task RunAsync(Func<CancellationToken, Task> onTick, CancellationToken stoppingToken);
}

/// <summary>
/// Drains all currently-due saga steps. The hosted service (or a test, or an
/// ops endpoint) invokes this; it is the single entry point the trigger fires.
/// Scoped — each tick resolves it in a fresh DI scope.
/// </summary>
public interface ISagaProcessor
{
    /// <summary>
    /// Claim and run every due step (bounded by <see cref="SagaOptions.BatchSize"/>),
    /// advancing / retrying / compensating / dead-lettering each. Returns the
    /// number of steps whose handler was actually invoked this tick. Never
    /// throws for a single step's failure — that is recorded durably.
    /// </summary>
    Task<int> ProcessDueStepsAsync(CancellationToken ct);
}
