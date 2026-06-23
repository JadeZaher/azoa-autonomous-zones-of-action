namespace AZOA.WebAPI.Sagas;

/// <summary>
/// Lifecycle state of a whole saga instance.
///
/// <para>This enum is intentionally storage-agnostic and bridge-agnostic — it is
/// the orchestration vocabulary every consumer (bridge, faucet batching,
/// webhook delivery, …) shares. A saga is the sequence of steps; the
/// per-step durable record drives execution (see <see cref="StepStatus"/>).</para>
/// </summary>
public enum SagaStatus
{
    /// <summary>At least one forward step is still pending/in-flight.</summary>
    Running = 0,

    /// <summary>Every forward step completed successfully — terminal success.</summary>
    Completed = 1,

    /// <summary>A forward step exhausted retries and the declared compensation
    /// chain ran to settle the saga back — terminal (compensated).</summary>
    Compensated = 2,

    /// <summary>A step (forward or compensation) exhausted retries with no
    /// further recourse — terminal failure parked in the dead-letter queue.</summary>
    DeadLettered = 3,
}

/// <summary>
/// Lifecycle state of a single durable saga step record. The conditional-claim
/// primitive (<c>WHERE Status==Pending AND NextRunAt&lt;=now</c>) and the
/// lease/visibility-timeout reclaim operate over exactly these states — mirrors
/// the proven api-safety-hardening conditional-UPDATE discipline.
/// </summary>
public enum StepStatus
{
    /// <summary>Due to run when <c>NextRunAt &lt;= now</c>. Claimable.</summary>
    Pending = 0,

    /// <summary>Atomically claimed by exactly one processor and executing.
    /// Becomes reclaimable again once the lease (visibility timeout) lapses —
    /// this is the crash-safe re-entry guarantee.</summary>
    InProgress = 1,

    /// <summary>The step's handler succeeded — terminal for this step. The saga
    /// either advances to the next step or completes.</summary>
    Completed = 2,

    /// <summary>A forward step exhausted its retry budget; its declared
    /// compensation step (itself a step record) has been enqueued. Terminal for
    /// the failed forward step.</summary>
    Compensating = 3,

    /// <summary>This step (forward or compensation) exhausted all retries and
    /// has no further recourse — parked in the dead-letter queue for an
    /// operator. Terminal.</summary>
    DeadLettered = 4,

    /// <summary>
    /// SUSPENDED on an external signal/timer (the durable-workflow-engine
    /// extension). A handler returned <see cref="StepResult.Park"/> requesting
    /// the run pause at this step until either a <c>SignalAsync(correlationKey,
    /// gateId, …)</c> un-parks it (gate node) or — when the park carried a
    /// forward <c>NextRunAt</c> — its timer becomes due (wait node).
    ///
    /// <para><b>Not terminal.</b> A parked step is invisible to the due-step
    /// claim scan (<c>WHERE status==Pending</c>) until signalled or its timer
    /// fires, at which point it returns to <see cref="Pending"/> (due now) and
    /// the processor resumes it. The un-park is itself a G2 single-winner
    /// conditional UPDATE so a duplicate signal un-parks at most once.</para>
    /// </summary>
    Parked = 5,
}
