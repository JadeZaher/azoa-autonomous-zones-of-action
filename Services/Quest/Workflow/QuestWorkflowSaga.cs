using OASIS.WebAPI.Sagas;

namespace OASIS.WebAPI.Services.Quest.Workflow;

/// <summary>
/// The durable-workflow-engine constants + the typed payload every quest
/// workflow saga step carries. A durable Quest run maps onto a saga instance
/// (durable-workflow-engine D1): the saga <c>CorrelationKey</c> = the run id and
/// each saga step's <c>StepName</c> = the quest node id it executes. One generic
/// <see cref="QuestNodeStepHandler"/> dispatches every node by reading the node
/// id from this payload, so the saga advances through an ARBITRARY per-run DAG
/// without a static forward-step list.
/// </summary>
public static class QuestWorkflowSaga
{
    /// <summary>The single registered saga name for every durable quest run.</summary>
    public const string Name = "quest-workflow";

    /// <summary>The one logical forward step (every node id resolves to it via
    /// <see cref="QuestWorkflowSagaDefinition.FindStep"/>).</summary>
    public const string NodeStepName = "quest-node";

    /// <summary>The per-run compensation step name (refund-on-failure). The
    /// compensation handler computes what to settle from the run's executed-node
    /// history — a per-RUN compensation, not a per-node static name, because the
    /// DAG is dynamic (durable-workflow-engine §5).</summary>
    public const string CompensateStepName = "quest-compensate";
}

/// <summary>
/// What one quest-workflow saga step hands its handler. The durable record
/// already carries <c>CorrelationKey</c> (= run id) and <c>StepName</c> (= node
/// id); these fields are duplicated typed so the handler need not re-parse, and
/// <see cref="QuestId"/>/<see cref="AvatarId"/> travel so the handler can load
/// the immutable quest definition and scope ownership without an extra run load
/// on the hot path. The handler constructs a FRESH payload per downstream node
/// when it self-advances (it never forwards the current payload unchanged).
/// </summary>
/// <param name="RunId">The durable Quest run (= saga correlation key).</param>
/// <param name="QuestId">The run's immutable quest definition (for edge walk +
/// node-handler context).</param>
/// <param name="AvatarId">The owning avatar (ownership scoping for value-moving
/// nodes — durable-workflow-engine cross-tenant scope).</param>
/// <param name="NodeId">The quest node THIS step executes (= parsed
/// <c>StepName</c>).</param>
/// <param name="SignalPayload">The external <c>signal(runId, gateId, payload)</c>
/// body carried into a resumed gate node; <c>null</c> on auto/manual hops.</param>
public sealed record QuestStepPayload(
    Guid RunId,
    Guid QuestId,
    Guid AvatarId,
    Guid NodeId,
    string? SignalPayload = null);

/// <summary>
/// The compensation step's payload. A DISTINCT type from
/// <see cref="QuestStepPayload"/> on purpose: <c>SagaStep&lt;T&gt;</c> resolves
/// its handler via <c>GetRequiredService&lt;IStepHandler&lt;T&gt;&gt;()</c> (by
/// closed generic type alone), so the forward node-step handler and the
/// compensation handler MUST close over different payload types to be
/// unambiguously DI-resolvable. Compensation is per-run (driven by the run's
/// executed-node history), so it needs only the run + its definition.
/// </summary>
/// <param name="RunId">The durable Quest run to settle back.</param>
/// <param name="QuestId">The run's quest definition.</param>
/// <param name="AvatarId">The owning avatar (ownership scoping for reversals).</param>
public sealed record QuestCompensatePayload(
    Guid RunId,
    Guid QuestId,
    Guid AvatarId);
