using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Sagas;

namespace OASIS.WebAPI.Services.Quest.Workflow;

/// <summary>
/// The single generic saga step handler that executes EVERY quest node in a
/// durable run (durable-workflow-engine D1, Approach A — self-advancing handler).
/// The saga dispatches this once per node; the handler:
///
/// <list type="number">
/// <item>checks the node's advancement marker (<c>Config._workflow</c>, D5) —
/// a <c>gated</c>/<c>timer</c> node PARKS before doing work
/// (<see cref="StepResult.Parked"/>);</item>
/// <item>otherwise CLAIMS the per-node <see cref="QuestNodeExecution"/> row
/// (<c>TryClaimPendingAsync</c>, the quest-temporal-fork-model G2 primitive) so
/// the node's effect runs at most once even if the saga step is re-dispatched
/// after a crash;</item>
/// <item>DISPATCHES the matching <see cref="IQuestNodeHandler"/> and records the
/// terminal execution state (guarded on <c>Running</c>);</item>
/// <item>SELF-ADVANCES: computes the single outgoing Control successor from the
/// run's DAG edges and enqueues it as a fresh saga step
/// (<c>ISagaStore.EnqueueNextStepAsync</c>), or — for a <c>manual</c> node —
/// suspends the run for a consumer <c>advance(...)</c>;</item>
/// <item>PROJECTS the <see cref="QuestRun.Status"/> read-model.</item>
/// </list>
///
/// <para>The three composed exactly-once guards: the saga claim
/// (<c>TryClaimDueStepAsync</c>, scheduling) → the node claim
/// (<c>TryClaimPendingAsync</c>, per-run-node once) → the step idempotency key
/// (the node handler's irreversible effect once). A re-dispatched step whose
/// node already executed is an idempotent replay: it re-drives advancement
/// without re-running the node.</para>
/// </summary>
public sealed class QuestNodeStepHandler : IStepHandler<QuestStepPayload>
{
    private readonly IQuestStore _questStore;
    private readonly IQuestRunStore _runStore;
    private readonly IQuestNodeExecutionStore _executionStore;
    private readonly IQuestNodeHandlerRegistry _registry;
    private readonly ISagaStore _sagaStore;
    private readonly ILogger<QuestNodeStepHandler> _logger;

    public QuestNodeStepHandler(
        IQuestStore questStore,
        IQuestRunStore runStore,
        IQuestNodeExecutionStore executionStore,
        IQuestNodeHandlerRegistry registry,
        ISagaStore sagaStore,
        ILogger<QuestNodeStepHandler> logger)
    {
        _questStore = questStore;
        _runStore = runStore;
        _executionStore = executionStore;
        _registry = registry;
        _sagaStore = sagaStore;
        _logger = logger;
    }

    public async Task<StepResult> ExecuteAsync(
        StepExecutionContext<QuestStepPayload> ctx, CancellationToken ct)
    {
        var p = ctx.Payload;

        var questResult = await _questStore.GetQuestAsync(p.QuestId, ct);
        if (questResult.IsError || questResult.Result is null)
            return StepResult.Fail(
                $"Quest {p.QuestId} not loadable for run {p.RunId}: {questResult.Message}");
        var quest = questResult.Result;

        var node = quest.Nodes.FirstOrDefault(n => n.Id == p.NodeId);
        if (node is null)
            return StepResult.Fail(
                $"Node {p.NodeId} not found in quest {p.QuestId} (run {p.RunId}).");

        var (advance, marker) = WorkflowNodeConfig.Parse(node.Config);

        // ── 1. Park BEFORE work for gate/timer nodes ──────────────────────────
        // A gate/wait node suspends the run until signalled or its timer fires.
        // The actual gate-predicate evaluation is the economic-primitive-nodes
        // track (D6); this engine only suspends/resumes. On resume (carried as
        // SignalPayload) the node falls through to do its work.
        if ((advance is WorkflowAdvance.Gated or WorkflowAdvance.Timer)
            && p.SignalPayload is null)
        {
            var gateId = marker?.GateId ?? node.Id.ToString();
            DateTime? resumeAt = advance is WorkflowAdvance.Timer
                ? DateTime.UtcNow.AddSeconds(Math.Max(1, marker?.ResumeInSeconds ?? 0))
                : null;

            await ProjectRunStatusAsync(
                p.RunId,
                advance is WorkflowAdvance.Timer
                    ? QuestRunStatus.AwaitingTimer
                    : QuestRunStatus.AwaitingSignal,
                ct);

            return StepResult.Parked(gateId, resumeAt);
        }

        // ── 2. Per-node exactly-once claim ────────────────────────────────────
        // The node may already have executed if this saga step is being
        // re-dispatched after a crash (the saga claim is reclaimable by lease).
        // The node claim is terminal (Pending → Running, one-way), so a lost
        // claim means "already ran" → idempotent replay: skip the node, re-drive
        // advancement.
        var claim = await _executionStore.TryClaimPendingAsync(p.RunId, p.NodeId, ct);
        if (claim.IsError)
            return StepResult.Fail(
                $"Node-execution claim failed for run {p.RunId} node {p.NodeId}: {claim.Message}");

        if (claim.Result is null)
        {
            // Lost the claim ⇒ the node already reached Running/terminal on a
            // prior attempt. Re-drive advancement idempotently from its recorded
            // outcome rather than re-running the effect.
            return await ReplayAdvancementAsync(quest, node, p, advance, ct);
        }

        var execution = claim.Result;
        await ProjectRunStatusAsync(p.RunId, QuestRunStatus.Running, ct);

        // ── 3. Dispatch the node handler ──────────────────────────────────────
        var upstream = await LoadUpstreamAsync(quest, p.NodeId, p.RunId, ct);
        QuestNodeHandlerResult result;
        if (!_registry.TryGet(node.NodeType, out var handler))
        {
            result = QuestNodeHandlerResult.Fail($"Unsupported node type: {node.NodeType}");
        }
        else
        {
            try
            {
                var nodeCtx = new QuestNodeExecutionContext(p.RunId, p.NodeId, quest, upstream);
                result = await handler.HandleAsync(nodeCtx, ct);
            }
            catch (Exception ex)
            {
                result = QuestNodeHandlerResult.Fail(ex.Message);
            }
        }

        // ── 4. Record terminal execution state (guarded on Running) ───────────
        if (result.IsError)
        {
            execution.State = QuestNodeState.Failed;
            execution.Error = result.Message;
        }
        else
        {
            execution.State = QuestNodeState.Succeeded;
            execution.Output = result.Output;
        }
        execution.EndedAt = DateTime.UtcNow;
        await _executionStore.UpdateAsync(execution, expectedState: QuestNodeState.Running, ct);

        // A failed node fails the saga step: the saga's retry/compensation
        // machinery takes over (refund-on-failure routes through the declared
        // CompensationStepName — durable-workflow-engine §5).
        if (result.IsError)
        {
            await ProjectRunStatusAsync(p.RunId, QuestRunStatus.Failed, ct);
            return StepResult.Fail(result.Message ?? $"Node {p.NodeId} failed.");
        }

        // ── 5. Self-advance ───────────────────────────────────────────────────
        return await AdvanceAsync(quest, node, p, advance, result.Output, ct);
    }

    /// <summary>
    /// Compute the single outgoing Control successor and enqueue it as a fresh
    /// saga step — or, for a <c>manual</c> node, suspend the run for a consumer
    /// <c>advance(...)</c>. A terminal node (no Control successors) completes the
    /// run. Fan-out (more than one Control successor) is rejected — fork-merge is
    /// an inherited non-goal (spec §Out of scope).
    /// </summary>
    private async Task<StepResult> AdvanceAsync(
        Models.Quest.Quest quest, QuestNode node, QuestStepPayload p,
        WorkflowAdvance advance, string? output, CancellationToken ct)
    {
        // A manual-advance node parks the run for an explicit consumer step():
        // do NOT enqueue the successor here. The run goes Suspended; the
        // QuestManager.AdvanceAsync path enqueues the successor on demand.
        if (advance is WorkflowAdvance.Manual)
        {
            await ProjectRunStatusAsync(p.RunId, QuestRunStatus.Suspended, ct);
            return StepResult.Ok(output);
        }

        var successors = NextControlNodeIds(quest, node.Id);

        if (successors.Count == 0)
        {
            // Terminal node ⇒ the run is complete (no Pending saga rows remain).
            await ProjectRunStatusAsync(p.RunId, QuestRunStatus.Succeeded, ct);
            return StepResult.Ok(output);
        }

        if (successors.Count > 1)
            return StepResult.Fail(
                $"Node {node.Id} has {successors.Count} Control successors — " +
                "fan-out is not supported (fork-merge is out of scope).");

        await EnqueueNodeAsync(p, successors[0], signalPayload: null, ct);
        await ProjectRunStatusAsync(p.RunId, QuestRunStatus.Running, ct);
        return StepResult.Ok(output);
    }

    /// <summary>
    /// Idempotent-replay advancement: the node already executed on a prior
    /// attempt of this saga step. Re-derive its recorded outcome and re-enqueue
    /// the downstream step if it is not already present, so a crash between
    /// "node executed" and "next step enqueued" is resumable, not lost.
    /// </summary>
    private async Task<StepResult> ReplayAdvancementAsync(
        Models.Quest.Quest quest, QuestNode node, QuestStepPayload p,
        WorkflowAdvance advance, CancellationToken ct)
    {
        var existing = await _executionStore.GetByRunAndNodeAsync(p.RunId, p.NodeId, ct);
        var state = existing.Result?.State;

        // The node is still Running on another attempt (or its row vanished) —
        // leave it for the lease reclaim; do not advance yet.
        if (state is null or QuestNodeState.Pending or QuestNodeState.Running)
            return StepResult.Ok(existing.Result?.Output);

        if (state is QuestNodeState.Failed)
        {
            await ProjectRunStatusAsync(p.RunId, QuestRunStatus.Failed, ct);
            return StepResult.Fail(existing.Result?.Error ?? $"Node {p.NodeId} failed.");
        }

        // Succeeded/Skipped ⇒ re-drive forward advancement idempotently.
        _logger.LogInformation(
            "Quest workflow: node {NodeId} (run {RunId}) already {State} — " +
            "idempotent replay, re-driving advancement.", p.NodeId, p.RunId, state);
        return await AdvanceAsync(quest, node, p, advance, existing.Result?.Output, ct);
    }

    /// <summary>Enqueue a downstream quest node as a fresh saga step (a fresh
    /// payload pointing at the next node — never the current payload forwarded
    /// unchanged).</summary>
    private async Task EnqueueNodeAsync(
        QuestStepPayload current, Guid nextNodeId, string? signalPayload, CancellationToken ct)
    {
        var nextPayload = current with { NodeId = nextNodeId, SignalPayload = signalPayload };
        var nextName = nextNodeId.ToString();
        var idemKey = SagaKeys.StepIdempotencyKey(current.RunId.ToString(), nextName);
        await _sagaStore.EnqueueNextStepAsync(
            QuestWorkflowSaga.Name, nextName, current.RunId.ToString(),
            idemKey, SagaStep<QuestStepPayload>.Serialize(nextPayload), ct);
    }

    /// <summary>Outgoing Control-edge target node ids (the DAG hop). Conditional
    /// edges are not forward hops here — they gate failed-predecessor skipping,
    /// the same role they play in the in-process executor.</summary>
    private static List<Guid> NextControlNodeIds(Models.Quest.Quest quest, Guid nodeId) =>
        quest.Edges
            .Where(e => e.SourceNodeId == nodeId && e.EdgeType == QuestEdgeType.Control)
            .Select(e => e.TargetNodeId)
            .Distinct()
            .ToList();

    private async Task<IReadOnlyDictionary<Guid, QuestNodeExecution>> LoadUpstreamAsync(
        Models.Quest.Quest quest, Guid nodeId, Guid runId, CancellationToken ct)
    {
        var upstream = new Dictionary<Guid, QuestNodeExecution>();
        var incoming = quest.Edges.Where(e => e.TargetNodeId == nodeId).ToList();
        if (incoming.Count == 0)
            return upstream;

        var all = await _executionStore.GetByRunIdAsync(runId, ct);
        if (all.IsError || all.Result is null)
            return upstream;

        var byNode = all.Result.ToDictionary(e => e.NodeId);
        foreach (var edge in incoming)
        {
            if (byNode.TryGetValue(edge.SourceNodeId, out var exec))
                upstream[edge.SourceNodeId] = exec;
        }
        return upstream;
    }

    /// <summary>
    /// Persist the <see cref="QuestRun.Status"/> read-model projection. Derived
    /// from saga-step transitions (the saga rows are the source of truth, D7) and
    /// idempotent: a non-terminal run can re-project on replay; a run already in
    /// a terminal state is never regressed.
    /// </summary>
    private async Task ProjectRunStatusAsync(Guid runId, QuestRunStatus status, CancellationToken ct)
    {
        var runResult = await _runStore.GetByIdAsync(runId, ct);
        if (runResult.IsError || runResult.Result is null)
            return;
        var run = runResult.Result;

        if (IsTerminal(run.Status))
            return; // never regress a terminal run

        if (run.Status == status)
            return; // idempotent no-op

        run.Status = status;
        if (IsTerminal(status))
            run.EndedAt = DateTime.UtcNow;
        await _runStore.UpdateAsync(run, ct);
    }

    private static bool IsTerminal(QuestRunStatus s) =>
        s is QuestRunStatus.Succeeded or QuestRunStatus.Failed
          or QuestRunStatus.Forked or QuestRunStatus.Cancelled;
}
