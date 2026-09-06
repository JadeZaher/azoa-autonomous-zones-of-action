namespace AZOA.WebAPI.Models.Actions;

/// <summary>
/// Lifecycle status of a single <see cref="ActionRun"/> (one execution attempt).
/// Kept distinct from <see cref="ActionStatus"/> (which carries the definition's
/// Draft/Active/Archived semantics) so run-level and definition-level lifecycle
/// never collide. See <c>conductor/tracks/action-temporal-fork-model/ADR.md</c>.
/// </summary>
/// <remarks>
/// Terminal states: <see cref="Succeeded"/>, <see cref="Failed"/>,
/// <see cref="Forked"/>, <see cref="Cancelled"/>. No further transitions occur
/// from any terminal state. <see cref="Forked"/> is a run-only concept and
/// MUST NOT appear in <see cref="ActionNodeState"/>.
/// </remarks>
public enum ActionRunStatus
{
    /// <summary>Run created; no node has been claimed yet.</summary>
    Pending,

    /// <summary>At least one node has transitioned out of Pending.</summary>
    Running,

    /// <summary>All terminal nodes reported Succeeded.</summary>
    Succeeded,

    /// <summary>Any node reported Failed, or supervisor marked the run failed.</summary>
    Failed,

    /// <summary>
    /// Parent run that was forked. New work continues on the child run referenced
    /// by another <see cref="ActionRun"/> whose <see cref="ActionRun.ParentRunId"/>
    /// equals this run's id. Terminal.
    /// </summary>
    Forked,

    /// <summary>Run was explicitly cancelled before completion.</summary>
    Cancelled,

    /// <summary>
    /// Run is SUSPENDED between nodes awaiting an explicit consumer
    /// <c>advance(runId, fromNodeId)</c> (the durable-workflow-engine
    /// manual-advance hop). Non-terminal: the run resumes when the consumer
    /// pushes the actor into the next phase. Durable — the suspension lives on
    /// the parked saga step + this projection, surviving a process restart.
    /// </summary>
    Suspended,

    /// <summary>
    /// Run is parked at a GATE node awaiting an external
    /// <c>signal(runId, gateId, payload)</c> (durable-workflow-engine).
    /// Non-terminal: a matching signal un-parks the gate and the engine resumes
    /// the DAG. Durable across restart.
    /// </summary>
    AwaitingSignal,

    /// <summary>
    /// Run is parked at a WAIT node until a timer becomes due
    /// (durable-workflow-engine). Non-terminal: the existing saga due-scan fires
    /// the parked step when its <c>NextRunAt</c> passes — no external call
    /// needed. Durable across restart.
    /// </summary>
    AwaitingTimer,

    /// <summary>
    /// Run is parked after a chain-action node failed with an indeterminate
    /// (Pending/Unknown) on-chain confirmation. Non-terminal: a reconciliation
    /// sweep (or operator) re-probes chain truth and resumes (Confirmed→advance)
    /// or fails (FailedOnChain). NEVER auto-retried — that would risk a
    /// double-broadcast.
    /// </summary>
    AwaitingReconciliation
}

/// <summary>
/// Helpers over <see cref="ActionRunStatus"/>. The single source of truth for
/// which run states are terminal — every projector (the workflow node-step
/// handler, the compensation handler, any future supervisor) must consult this
/// rather than re-listing the terminal set, so adding a lifecycle state can
/// never leave one projector's terminal-guard stale.
/// </summary>
public static class ActionRunStatusExtensions
{
    /// <summary>
    /// A terminal run accepts no further transitions: <see cref="ActionRunStatus.Succeeded"/>,
    /// <see cref="ActionRunStatus.Failed"/>, <see cref="ActionRunStatus.Forked"/>,
    /// <see cref="ActionRunStatus.Cancelled"/>. The Suspended/AwaitingSignal/
    /// AwaitingTimer states are explicitly NON-terminal (the run resumes).
    /// </summary>
    public static bool IsTerminal(this ActionRunStatus status) =>
        status is ActionRunStatus.Succeeded or ActionRunStatus.Failed
               or ActionRunStatus.Forked or ActionRunStatus.Cancelled;
}
