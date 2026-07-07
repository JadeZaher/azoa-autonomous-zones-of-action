using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Workflow;

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
    private readonly IWalletManager _walletManager;
    private readonly IBlockchainProviderFactory _chainFactory;
    private readonly QuestConfigBindingResolver _bindingResolver;
    private readonly ILogger<QuestNodeStepHandler> _logger;

    public QuestNodeStepHandler(
        IQuestStore questStore,
        IQuestRunStore runStore,
        IQuestNodeExecutionStore executionStore,
        IQuestNodeHandlerRegistry registry,
        ISagaStore sagaStore,
        IWalletManager walletManager,
        IBlockchainProviderFactory chainFactory,
        QuestConfigBindingResolver bindingResolver,
        ILogger<QuestNodeStepHandler> logger)
    {
        _questStore = questStore;
        _runStore = runStore;
        _executionStore = executionStore;
        _registry = registry;
        _sagaStore = sagaStore;
        _walletManager = walletManager;
        _chainFactory = chainFactory;
        _bindingResolver = bindingResolver;
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
            if (advance is WorkflowAdvance.Timer)
            {
                // A pure WAIT node parks as a TIMER park (resumeAt set, no gate):
                // the store records gate_id NONE + next_run_at, and the
                // fire-timers scan auto-resumes it with no external signal. The
                // resumeAt presence IS the timer/signal discriminator — gate id
                // is meaningless for a timer, so none is supplied.
                var resumeAt = DateTime.UtcNow.AddSeconds(Math.Max(1, marker?.ResumeInSeconds ?? 0));
                await ProjectRunStatusAsync(p.RunId, QuestRunStatus.AwaitingTimer, ct);
                return StepResult.Parked(gateId: string.Empty, resumeAt: resumeAt);
            }

            // A GATE node parks as a SIGNAL park (gate id, no timer): only
            // signal(runId, gateId, …) un-parks it.
            var gateId = marker?.GateId ?? node.Id.ToString();
            await ProjectRunStatusAsync(p.RunId, QuestRunStatus.AwaitingSignal, ct);
            return StepResult.Parked(gateId, resumeAt: null);
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

        // C1/H1: the acting avatar is the RUNNER (run.AvatarId), which may differ
        // from quest.AvatarId on a marketplace run. Load the run once here so the
        // capability gate, $from binding, and node context all derive identity from
        // it — never from the quest owner. Fail closed if the run can't be loaded.
        var runResult = await _runStore.GetByIdAsync(p.RunId, ct);
        if (runResult.IsError || runResult.Result is null)
            return StepResult.Fail(
                $"Run {p.RunId} not loadable for node {p.NodeId}: {runResult.Message}");
        var run = runResult.Result;
        var actingAvatarId = run.AvatarId;

        // ── 3. Dispatch the node handler ──────────────────────────────────────
        var (upstream, allRunExecutions) = await LoadRunExecutionsAsync(quest, p.NodeId, p.RunId, ct);
        QuestNodeHandlerResult result;
        // Whether the dispatched node is a chain-action node: only such nodes go
        // through reconcile-before-retry on failure (a non-chain node has no tx to
        // reconcile, so it falls straight through to the saga retry/compensation).
        var handlerRequiresChain = false;
        if (!_registry.TryGet(node.NodeType, out var handler))
        {
            result = QuestNodeHandlerResult.Fail($"Unsupported node type: {node.NodeType}");
        }
        else if ((handlerRequiresChain = handler.RequiresChainCapability)
            && !await ChainCapabilityGate.HasWalletBoundAsync(_walletManager, actingAvatarId, ct))
        {
            // D1 pre-execution capability gate — fails closed (no broadcast):
            // a chain-requiring node may not run unless the actor has a wallet
            // bound. HandleAsync is SKIPPED, so the durable path cannot bypass
            // the gate the legacy executor also enforces.
            result = QuestNodeHandlerResult.Fail(ChainCapabilityGate.NoWalletBoundMessage);
        }
        else
        {
            // FR-1 ($from binding) — resolve before handler dispatch (durable path).
            // See Services/Quest/AGENTS.md §output-binding. H1: holon-scoped binding
            // reads resolve against the RUNNER (actingAvatarId), not the quest owner.
            var actingTenantId = run.ActingTenantId;

            var bindingResult = await _bindingResolver.TryResolveAsync(
                node.Config, node, quest, upstream, allRunExecutions, actingAvatarId, ct);

            if (!bindingResult.Ok)
            {
                result = QuestNodeHandlerResult.Fail($"$from binding error on node '{node.Name}': {bindingResult.Error}");
            }
            else
            {
                try
                {
                    // tenant-consent-delegation AC4: read the acting tenant off the
                    // durable run (persisted at activation by StartWorkflowRunAsync) and
                    // carry it into the node context. This is the seam where the acting
                    // tenant re-enters the async saga path — it is NOT ambient on the
                    // worker, so it MUST come from the persisted run. A user-driven run
                    // has ActingTenantId = null → identical behaviour to before.
                    var originalConfig = node.Config;
                    // Ok==true (checked above) guarantees ResolvedJson is non-null.
                    node.Config = bindingResult.ResolvedJson!;
                    try
                    {
                        var nodeCtx = new QuestNodeExecutionContext(p.RunId, p.NodeId, quest, actingAvatarId, upstream, actingTenantId);
                        result = await handler.HandleAsync(nodeCtx, ct);
                    }
                    finally
                    {
                        node.Config = originalConfig;
                    }
                }
                catch (Exception ex)
                {
                    result = QuestNodeHandlerResult.Fail(ex.Message);
                }
            }
        }

        // ── 3a. Record the broadcast tx hash BEFORE confirmation resolves ─────
        // The reconcile-before-retry guarantee's first clause
        // (blockchain-recovery-and-portable-wallets §1.1 / P7 owed-item 1): a
        // chain-action handler that put a tx on the wire stamps the hash on the
        // STILL-Running execution row immediately — before the reconcile probe runs
        // and before the terminal write. So even if this step crashes between the
        // handler return and the terminal/park write, the row already carries the
        // hash for a later sweep to reconcile against (otherwise a crash here would
        // strand a broadcast tx with no recorded hash → the run could never be
        // safely reconciled, only parked-then-stuck). The row stays Running, so the
        // later guarded terminal write (expectedState: Running) still matches.
        if (handlerRequiresChain && !string.IsNullOrWhiteSpace(result.TxHash)
            && execution.State == QuestNodeState.Running)
        {
            execution.TxHash = result.TxHash;
            execution.ChainType = result.ChainType;
            var stamped = await _executionStore.UpdateAsync(
                execution, expectedState: QuestNodeState.Running, ct);
            if (stamped.IsError)
                // The row drifted off Running underneath us (lease-reclaimed
                // sibling, fork-cancel) — re-drive from the durable outcome rather
                // than continuing on a stale in-memory row.
                return await ReplayAdvancementAsync(quest, node, p, advance, ct);
        }

        // ── 3b. Reconcile-before-retry for chain-action nodes ─────────────────
        // A chain-action node (Grant/Transfer/Swap/FungibleTokenCreate) that
        // FAILED must NOT be blind-retried: attempt 1 may have broadcast and
        // landed even though the handler reported an error (e.g. the confirmation
        // read timed out). Re-running would double-mint/double-spend. Instead we
        // verify the broadcast tx against chain truth and act on the verdict
        // (blockchain-recovery-and-portable-wallets §1.4). Non-chain nodes and the
        // success path skip this entirely.
        if (result.IsError && handlerRequiresChain)
        {
            var reconciled = await ReconcileChainFailureAsync(
                quest, node, p, advance, execution, result, ct);
            if (reconciled is { } outcome)
                return outcome;
            // null ⇒ the verdict is Retry: the tx provably failed on-chain, so
            // re-broadcast is safe — fall through to the normal record-Failed +
            // StepResult.Fail path so the saga retry/compensation owns it.
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
            // A successful chain-action node carries its broadcast hash too, so the
            // execution row is a complete on-chain audit record (and a later sweep
            // never mistakes a stamped-Succeeded node for one needing reconciliation).
            execution.TxHash = result.TxHash;
            execution.ChainType = result.ChainType;
        }
        execution.EndedAt = DateTime.UtcNow;
        var recorded = await _executionStore.UpdateAsync(
            execution, expectedState: QuestNodeState.Running, ct);
        if (recorded.IsError)
        {
            // The guarded write lost: a concurrent actor (a lease-reclaimed
            // sibling dispatch, a fork-cancel) already moved this execution off
            // Running. Our in-memory `result` is stale — do NOT advance off it.
            // Re-drive from the durably-recorded outcome, which is the single
            // source of truth (idempotent replay).
            return await ReplayAdvancementAsync(quest, node, p, advance, ct);
        }

        // A failed node fails the saga step: the saga's retry/compensation
        // machinery takes over (refund-on-failure routes through the declared
        // CompensationStepName — durable-workflow-engine §5). Do NOT project a
        // terminal Failed here — the node-step ALWAYS declares a compensation,
        // so a failing attempt is not yet the run's verdict. The run stays in
        // flight (Running) while it retries; the terminal projection is owned
        // downstream: the compensate handler settles it Cancelled, or — if
        // compensation itself dead-letters — it stays Running for an operator.
        // Pre-empting with Failed here would suppress the Cancelled projection
        // (the terminal-guard would never let compensation overwrite it).
        if (result.IsError)
        {
            // V5: when the failed node has an OnFailure successor, advance to it
            // instead of surfacing the saga failure. This keeps the run Running
            // and lets the failure arm complete without triggering compensation.
            // See Services/Quest/Workflow/AGENTS.md §skip-semantics.
            var onFailureHop = QuestWorkflowEdges.ResolveOnFailureSuccessor(quest, node.Id);
            if (onFailureHop.Kind == SuccessorKind.Single)
                return await AdvanceOnFailureAsync(p, onFailureHop.NodeId!.Value, ct);

            return StepResult.Fail(result.Message ?? $"Node {p.NodeId} failed.");
        }

        // ── 5. Self-advance ───────────────────────────────────────────────────
        return await AdvanceAsync(quest, node, p, advance, result.Output, ct);
    }

    /// <summary>
    /// Enqueues the OnFailure successor and keeps the run Running. The failure
    /// arm takes over normal forward progression; from the saga's perspective the
    /// step succeeds (no compensation triggered). V5 — both fresh and replay seams
    /// call this. See Services/Quest/Workflow/AGENTS.md §skip-semantics.
    /// </summary>
    private async Task<StepResult> AdvanceOnFailureAsync(
        QuestStepPayload p, Guid onFailureNodeId, CancellationToken ct)
    {
        await EnqueueNodeAsync(p, onFailureNodeId, signalPayload: null, ct);
        await ProjectRunStatusAsync(p.RunId, QuestRunStatus.Running, ct);
        return StepResult.Ok(null);
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

        var hop = QuestWorkflowEdges.ResolveSingleSuccessor(quest, node.Id);
        switch (hop.Kind)
        {
            case SuccessorKind.Terminal:
                // No Control successors ⇒ the run is complete (no Pending saga
                // rows remain).
                await ProjectRunStatusAsync(p.RunId, QuestRunStatus.Succeeded, ct);
                return StepResult.Ok(output);

            case SuccessorKind.FanOut:
                return StepResult.Fail(
                    $"Node {node.Id} has {hop.Count} Control successors — " +
                    "fan-out is not supported (fork-merge is out of scope).");

            default: // Single
                await EnqueueNodeAsync(p, hop.NodeId!.Value, signalPayload: null, ct);
                await ProjectRunStatusAsync(p.RunId, QuestRunStatus.Running, ct);
                return StepResult.Ok(output);
        }
    }

    /// <summary>
    /// Reconcile-before-retry for a FAILED chain-action node
    /// (blockchain-recovery-and-portable-wallets §1.4). Probes the broadcast tx
    /// against chain truth and returns the saga outcome:
    ///
    /// <list type="bullet">
    /// <item><b>Invalid config</b> (<c>Retriable == false</c>): nothing was
    /// broadcast and re-running can never succeed — record Failed and surface a
    /// <see cref="StepResult.Fail"/> with NO chain probe. (Fail, not a silent
    /// drop, so the declared compensation still settles the run; because nothing
    /// was on the wire a re-attempt is side-effect-free, so the retry budget
    /// ticking is harmless.)</item>
    /// <item><b>AdvanceReconciled</b> (Confirmed): the tx LANDED — treat as
    /// success. Record the execution Succeeded with the handler's original output
    /// and the tx hash stamped, then self-advance exactly as the success path.</item>
    /// <item><b>ParkForReconciliation</b> (Pending/Unknown, or no tx hash):
    /// re-broadcasting could double-spend. Project the run to
    /// <see cref="QuestRunStatus.AwaitingReconciliation"/>, record the execution
    /// Failed WITH the tx hash stamped (so the future sweep can find it), and PARK
    /// the saga step. See the park-choice note below.</item>
    /// </list>
    ///
    /// <para>Returns <c>null</c> for the <b>Retry</b> verdict (provably
    /// FailedOnChain) so the caller falls through to the normal record-Failed +
    /// <see cref="StepResult.Fail"/> path that hands the outcome to the saga's
    /// retry/compensation budget.</para>
    ///
    /// <para><b>Park-choice justification — why this cannot double-broadcast.</b>
    /// The park uses <c>resumeAt: null</c> (suspend until signalled, NO timer), so
    /// the saga due-scan never auto-re-dispatches the step — the run sits in
    /// AwaitingReconciliation for the dedicated reconciliation sweep / an operator
    /// to resolve. Even if the step WERE re-dispatched (a future sweep signalling
    /// the gate, or a lease reclaim), it re-enters <see cref="ExecuteAsync"/> at
    /// the per-node exactly-once claim (<c>TryClaimPendingAsync</c>): the execution
    /// row is already off <c>Pending</c> (we recorded it Failed here), so the claim
    /// is lost and control routes to <see cref="ReplayAdvancementAsync"/> — the
    /// broadcasting <c>handler.HandleAsync</c> is NEVER reached again for this
    /// (run, node). The node claim is the structural backstop that makes a second
    /// broadcast impossible; the <c>resumeAt: null</c> park is the policy choice
    /// that also avoids even re-dispatching the step until truth is known.</para>
    /// </summary>
    private async Task<StepResult?> ReconcileChainFailureAsync(
        Models.Quest.Quest quest, QuestNode node, QuestStepPayload p,
        WorkflowAdvance advance, QuestNodeExecution execution,
        QuestNodeHandlerResult result, CancellationToken ct)
    {
        // Invalid config — provably nothing broadcast; never probe, never park.
        if (!result.Retriable)
        {
            _logger.LogWarning(
                "Quest workflow: chain-action node {NodeId} (run {RunId}) failed with an " +
                "INVALID-config result — terminal fail, no chain probe (nothing was broadcast).",
                p.NodeId, p.RunId);
            execution.State = QuestNodeState.Failed;
            execution.Error = result.Message;
            execution.EndedAt = DateTime.UtcNow;
            var rec = await _executionStore.UpdateAsync(
                execution, expectedState: QuestNodeState.Running, ct);
            if (rec.IsError)
                return await ReplayAdvancementAsync(quest, node, p, advance, ct);
            return StepResult.Fail(result.Message ?? $"Node {p.NodeId} failed (invalid config).");
        }

        // Probe chain truth. A missing/errored probe folds to Unknown in the
        // provider default — never a false negative — so a flaky RPC parks rather
        // than triggering a re-broadcast.
        var verdict = ChainConfirmation.Unknown;
        if (!string.IsNullOrWhiteSpace(result.TxHash))
        {
            var provider = ResolveProvider(result.ChainType);
            if (provider != null)
            {
                var conf = await provider.GetTransactionConfirmationAsync(result.TxHash!, ct);
                // On a probe error keep the initialized Unknown ⇒ park. Only adopt
                // the verdict from a non-errored result; the provider base default
                // already folds an errored/absent status into Unknown internally,
                // so this is belt-and-suspenders against a false Confirmed.
                if (!conf.IsError)
                    verdict = conf.Result;
            }
        }

        var action = ChainActionRecovery.Decide(result.TxHash, verdict);
        switch (action)
        {
            case ChainActionRecoveryAction.AdvanceReconciled:
            {
                // The tx LANDED (Confirmed). The effect is done — reconcile to
                // success: record Succeeded with the original output + tx hash, then
                // self-advance exactly as the success path would.
                _logger.LogInformation(
                    "Quest workflow: chain-action node {NodeId} (run {RunId}) reported failure but " +
                    "tx {TxHash} is CONFIRMED on-chain — reconciled to SUCCESS (no retry).",
                    p.NodeId, p.RunId, result.TxHash);
                execution.State = QuestNodeState.Succeeded;
                execution.Output = result.Output;
                execution.TxHash = result.TxHash;
                execution.ChainType = result.ChainType;
                execution.EndedAt = DateTime.UtcNow;
                var rec = await _executionStore.UpdateAsync(
                    execution, expectedState: QuestNodeState.Running, ct);
                if (rec.IsError)
                    return await ReplayAdvancementAsync(quest, node, p, advance, ct);
                return await AdvanceAsync(quest, node, p, advance, result.Output, ct);
            }

            case ChainActionRecoveryAction.ParkForReconciliation:
            {
                // Pending/Unknown (or no tx hash): re-broadcasting could
                // double-spend. Stamp the tx hash on a Failed execution row so the
                // future reconciliation sweep can locate it, project the run to
                // AwaitingReconciliation, and PARK the step (resumeAt: null — see
                // method-doc park-choice justification: this neither re-runs the
                // broadcasting handler nor consumes a retry attempt).
                _logger.LogWarning(
                    "Quest workflow: chain-action node {NodeId} (run {RunId}) failed with an " +
                    "INDETERMINATE on-chain verdict ({Verdict}, tx '{TxHash}') — PARKING in " +
                    "AwaitingReconciliation (no retry, no re-broadcast).",
                    p.NodeId, p.RunId, verdict, result.TxHash ?? "<none>");
                execution.State = QuestNodeState.Failed;
                execution.Error = result.Message;
                execution.TxHash = result.TxHash;
                execution.ChainType = result.ChainType;
                execution.EndedAt = DateTime.UtcNow;
                var rec = await _executionStore.UpdateAsync(
                    execution, expectedState: QuestNodeState.Running, ct);
                if (rec.IsError)
                    return await ReplayAdvancementAsync(quest, node, p, advance, ct);

                await ProjectRunStatusAsync(p.RunId, QuestRunStatus.AwaitingReconciliation, ct);
                // Stable, node-scoped gate id so a future sweep can signal THIS
                // parked step precisely. resumeAt null ⇒ no timer auto-resume.
                var reconGateId = $"recon:{p.NodeId}";
                return StepResult.Parked(reconGateId, resumeAt: null);
            }

            case ChainActionRecoveryAction.Retry:
            default:
                // Provably FailedOnChain — re-broadcast is safe. Fall through to the
                // caller's normal record-Failed + StepResult.Fail path.
                return null;
        }
    }

    /// <summary>
    /// Resolve the chain provider for reconciliation, mirroring
    /// <c>ReconciliationService</c>: by chain type when known, else the configured
    /// default. Network is not authoritatively carried on the execution row, so the
    /// configured default network (Devnet) is used — the factory caches per
    /// chain:network. A resolution failure returns null ⇒ the caller leaves the
    /// verdict Unknown and parks (never a false "failed" that would re-broadcast).
    /// </summary>
    private IBlockchainProvider? ResolveProvider(string? chainType)
    {
        try
        {
            return string.IsNullOrWhiteSpace(chainType)
                ? _chainFactory.GetDefaultProvider()
                : _chainFactory.GetProvider(chainType, ChainNetwork.Devnet);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Quest workflow: reconciliation provider resolution failed for chain '{Chain}' — " +
                "leaving verdict Unknown (will park).", chainType ?? "<default>");
            return null;
        }
    }

    /// <summary>
    /// Idempotent-replay advancement: the node already executed on a prior
    /// attempt of this saga step. Re-derive its recorded outcome and re-drive
    /// advancement — the downstream enqueue is guarded by
    /// <see cref="EnqueueNodeAsync"/>'s <c>StepExistsAsync</c> check, so a crash
    /// between "node executed" and "next step enqueued" is resumable WITHOUT
    /// creating a duplicate successor.
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
            // V5 replay: same OnFailure short-circuit as the forward path.
            var onFailureHop = QuestWorkflowEdges.ResolveOnFailureSuccessor(quest, node.Id);
            if (onFailureHop.Kind == SuccessorKind.Single)
                return await AdvanceOnFailureAsync(p, onFailureHop.NodeId!.Value, ct);

            // No OnFailure successor — re-fail so saga retry/compensation machinery owns it.
            return StepResult.Fail(existing.Result?.Error ?? $"Node {p.NodeId} failed.");
        }

        // Succeeded/Skipped ⇒ re-drive forward advancement idempotently.
        _logger.LogInformation(
            "Quest workflow: node {NodeId} (run {RunId}) already {State} — " +
            "idempotent replay, re-driving advancement.", p.NodeId, p.RunId, state);
        return await AdvanceAsync(quest, node, p, advance, existing.Result?.Output, ct);
    }

    /// <summary>
    /// Enqueue a downstream quest node as a fresh saga step (a fresh payload
    /// pointing at the next node — never the current payload forwarded unchanged).
    /// IDEMPOTENT: a replayed advance (the producing step re-dispatched after a
    /// crash, then routed through <see cref="ReplayAdvancementAsync"/>) must not
    /// CREATE a second successor row — <c>step_idempotency_key</c> is deliberately
    /// non-unique, so a duplicate enqueue would amplify the DAG with phantom
    /// steps. We check the saga instance for an existing step of this node name
    /// first; the guard is the run-scoped one-step-per-node-name invariant (a
    /// quest node maps to exactly one saga step per run).
    /// </summary>
    private async Task EnqueueNodeAsync(
        QuestStepPayload current, Guid nextNodeId, string? signalPayload, CancellationToken ct)
    {
        var correlationKey = current.RunId.ToString();
        var nextName = nextNodeId.ToString();

        if (await _sagaStore.StepExistsAsync(correlationKey, nextName, ct))
        {
            _logger.LogInformation(
                "Quest workflow: successor node {NodeId} (run {RunId}) already enqueued — " +
                "skipping duplicate (idempotent replay).", nextNodeId, current.RunId);
            return;
        }

        var nextPayload = current with { NodeId = nextNodeId, SignalPayload = signalPayload };
        var idemKey = SagaKeys.StepIdempotencyKey(correlationKey, nextName);
        await _sagaStore.EnqueueNextStepAsync(
            QuestWorkflowSaga.Name, nextName, correlationKey,
            idemKey, SagaStep<QuestStepPayload>.Serialize(nextPayload), ct);
    }

    /// <summary>
    /// Loads, in one store round-trip, both the direct-upstream execution map
    /// (incoming-edge sources, for the <c>upstream.</c> root) and the full
    /// run-execution map keyed by node id (for the run-scoped <c>run.</c> root).
    /// See Services/Quest/AGENTS.md §output-binding.
    /// </summary>
    private async Task<(IReadOnlyDictionary<Guid, QuestNodeExecution> Upstream,
                        IReadOnlyDictionary<Guid, QuestNodeExecution> AllByNode)>
        LoadRunExecutionsAsync(Models.Quest.Quest quest, Guid nodeId, Guid runId, CancellationToken ct)
    {
        var upstream = new Dictionary<Guid, QuestNodeExecution>();

        var all = await _executionStore.GetByRunIdAsync(runId, ct);
        if (all.IsError || all.Result is null)
            return (upstream, new Dictionary<Guid, QuestNodeExecution>());

        var byNode = all.Result.ToDictionary(e => e.NodeId);
        foreach (var edge in quest.Edges.Where(e => e.TargetNodeId == nodeId))
        {
            if (byNode.TryGetValue(edge.SourceNodeId, out var exec))
                upstream[edge.SourceNodeId] = exec;
        }
        return (upstream, byNode);
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

        if (run.Status.IsTerminal())
            return; // never regress a terminal run

        if (run.Status == status)
            return; // idempotent no-op

        var expected = run.Status;
        run.Status = status;
        if (status.IsTerminal())
            run.EndedAt = DateTime.UtcNow;
        // Conditional on the status we just read: a concurrent projector that
        // moved the run between our read and write loses (zero-row no-op), so the
        // read-model can't be clobbered or a terminal verdict regressed. A lost
        // write is benign — the winning projector already advanced the run.
        await _runStore.UpdateAsync(run, expectedStatus: expected, ct);
    }
}
