using AZOA.WebAPI.Sagas;

namespace AZOA.WebAPI.Services.Quest.Workflow;

/// <summary>
/// The single registered <see cref="ISagaDefinition"/> for every durable Quest
/// run (durable-workflow-engine D1, Approach A — the self-advancing handler).
///
/// <para><b>Type-uniform step resolution.</b> Unlike <see cref="SagaDefinition"/>
/// (whose <c>FindStep</c> is a name-keyed dictionary lookup), this definition
/// resolves EVERY step name — each is a quest node id GUID — to the SAME
/// <see cref="SagaStep{T}"/> instance. That is what lets one registered handler
/// dispatch an arbitrary per-run DAG: the saga step name is the node id, and the
/// handler reads it from the payload to select the right
/// <see cref="AZOA.WebAPI.Interfaces.QuestExecution.IQuestNodeHandler"/>.</para>
///
/// <para><b>Handler-driven advancement.</b> <see cref="NextForwardStep"/> always
/// returns <c>null</c> so <c>SagaProcessor</c> never advances the saga itself;
/// the node-step handler computes the next node(s) from the run's DAG edges and
/// enqueues them via <c>ISagaStore.EnqueueNextStepAsync</c>. The processor's
/// "final forward step completed" log fires harmlessly after each node.</para>
///
/// <para><b>Start path.</b> A run is NOT started through
/// <see cref="ISagaCoordinator"/> (its <c>is SagaDefinition</c> concrete cast
/// would reject this type). The manager calls <c>ISagaStore.EnqueueAsync</c>
/// directly with the entry node id as the first step name.</para>
///
/// <para>The shared node-step declares <see cref="QuestWorkflowSaga.CompensateStepName"/>
/// as its compensation: a forward node that exhausts retries routes through the
/// saga's first-class <c>CompensateStepAsync</c>, and the compensation handler
/// settles the run from its executed-node history.</para>
/// </summary>
public sealed class QuestWorkflowSagaDefinition : ISagaDefinition
{
    private readonly ISagaStep _nodeStep;
    private readonly ISagaStep _compensateStep;

    public QuestWorkflowSagaDefinition()
    {
        _nodeStep = new SagaStep<QuestStepPayload>(
            QuestWorkflowSaga.NodeStepName,
            retryPolicy: RetryPolicy.Default,
            compensationStepName: QuestWorkflowSaga.CompensateStepName);

        // The compensation step itself carries no further compensation (it
        // dead-letters if it in turn exhausts) — same contract as any
        // SagaDefinition compensation step. It closes over a DISTINCT payload
        // type so its handler is unambiguously DI-resolvable (see
        // QuestCompensatePayload). The forward step's QuestStepPayload JSON
        // deserializes cleanly into QuestCompensatePayload (subset of fields).
        _compensateStep = new SagaStep<QuestCompensatePayload>(
            QuestWorkflowSaga.CompensateStepName,
            retryPolicy: RetryPolicy.Default,
            compensationStepName: null);
    }

    public string Name => QuestWorkflowSaga.Name;

    /// <summary>The single logical forward step. The real fan-out is the DAG,
    /// driven by the handler — not this list.</summary>
    public IReadOnlyList<ISagaStep> ForwardSteps => new[] { _nodeStep };

    /// <summary>
    /// Every node-id step name resolves to the one node-step; the compensation
    /// step name resolves to the compensation step. This type-uniform resolution
    /// is the core of Approach A.
    /// </summary>
    public ISagaStep? FindStep(string stepName) =>
        stepName == QuestWorkflowSaga.CompensateStepName ? _compensateStep : _nodeStep;

    /// <summary>
    /// Always <c>null</c>: the handler self-advances by enqueuing the downstream
    /// node directly, so the processor's static-list advancement is intentionally
    /// disabled for this saga.
    /// </summary>
    public ISagaStep? NextForwardStep(string stepName) => null;
}
