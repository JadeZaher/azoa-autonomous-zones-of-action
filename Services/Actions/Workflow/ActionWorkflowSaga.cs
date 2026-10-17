
namespace AZOA.WebAPI.Services.Actions.Workflow;

/// <summary>
/// The durable-workflow-engine constants + the typed payload every action
/// workflow saga step carries. A durable action run maps onto a saga instance
/// (durable-workflow-engine D1): the saga <c>CorrelationKey</c> = the run id and
/// each saga step's <c>StepName</c> = the action node id it executes. One generic
/// <see cref="ActionNodeStepHandler"/> dispatches every node by reading the node
/// id from this payload, so the saga advances through an ARBITRARY per-run DAG
/// without a static forward-step list.
/// </summary>
public static class ActionWorkflowSaga
{
    /// <summary>The single registered saga name for every durable action run.</summary>
    public const string Name = "action-workflow";

    /// <summary>The one logical forward step (every node id resolves to it via
    /// <see cref="ActionWorkflowSagaDefinition.FindStep"/>).</summary>
    public const string NodeStepName = "action-node";

    /// <summary>The per-run compensation step name (refund-on-failure). The
    /// compensation handler computes what to settle from the run's executed-node
    /// history — a per-RUN compensation, not a per-node static name, because the
    /// DAG is dynamic (durable-workflow-engine §5).</summary>
    public const string CompensateStepName = "action-compensate";
}

/// <summary>
/// What one action-workflow saga step hands its handler. The durable record
/// already carries <c>CorrelationKey</c> (= run id) and <c>StepName</c> (= node
/// id); these fields are duplicated typed so the handler need not re-parse, and
/// <see cref="ActionId"/>/<see cref="UserId"/> travel so the handler can load
/// the immutable action definition and scope ownership without an extra run load
/// on the hot path. The handler constructs a FRESH payload per downstream node
/// when it self-advances (it never forwards the current payload unchanged).
/// </summary>
/// <param name="RunId">The durable action run (= saga correlation key).</param>
/// <param name="ActionId">The run's immutable action definition (for edge walk +
/// node-handler context).</param>
/// <param name="UserId">The owning user (ownership scoping for value-moving
/// nodes — durable-workflow-engine cross-tenant scope).</param>
/// <param name="NodeId">The action node THIS step executes (= parsed
/// <c>StepName</c>).</param>
/// <param name="SignalPayload">The external <c>signal(runId, gateId, payload)</c>
/// body carried into a resumed gate node; <c>null</c> on auto/manual hops.</param>
public sealed record ActionStepPayload(
    Guid RunId,
    Guid ActionId,
    Guid UserId,
    Guid NodeId,
    string? SignalPayload = null)
{
    /// <summary>
    /// Project the shared identity triple onto the compensation payload. The
    /// saga's <c>CompensateStepAsync</c> actually flows the forward payload's
    /// JSON verbatim and lets <c>ActionCompensatePayload</c> deserialize the
    /// matching fields — this method is the COMPILE-TIME witness of that
    /// field correspondence (and the constructor any future direct caller
    /// should use), so a rename of a shared field is caught here and by the
    /// round-trip test rather than silently yielding <c>Guid.Empty</c>.
    /// </summary>
    public ActionCompensatePayload ToCompensation() => new(RunId, ActionId, UserId);
}

/// <summary>
/// The compensation step's payload. A DISTINCT type from
/// <see cref="ActionStepPayload"/> on purpose: <c>SagaStep&lt;T&gt;</c> resolves
/// its handler via <c>GetRequiredService&lt;IStepHandler&lt;T&gt;&gt;()</c> (by
/// closed generic type alone), so the forward node-step handler and the
/// compensation handler MUST close over different payload types to be
/// unambiguously DI-resolvable. Compensation is per-run (driven by the run's
/// executed-node history), so it needs only the run + its definition.
/// </summary>
/// <param name="RunId">The durable action run to settle back.</param>
/// <param name="ActionId">The run's action definition.</param>
/// <param name="UserId">The owning user (ownership scoping for reversals).</param>
public sealed record ActionCompensatePayload(
    Guid RunId,
    Guid ActionId,
    Guid UserId);
