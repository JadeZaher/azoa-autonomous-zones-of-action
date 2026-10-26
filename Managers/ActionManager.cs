using System.Text.Json;

using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Workflow;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Orchestrates action execution against the per-run / per-(run,node) runtime
/// surface introduced by the <c>action-temporal-fork-model</c> track. Holds a
/// <see cref="IActionStore"/> (definitions, immutable in-flight),
/// <see cref="IActionRunStore"/> (one row per execution attempt),
/// <see cref="IActionNodeExecutionStore"/> (per-(run, node) state/output/error),
/// and <see cref="IActionNodeHandlerRegistry"/> (the per-<see cref="ActionNodeType"/>
/// dispatch table).
/// </summary>
public partial class ActionManager : IActionManager, IParkedRunReconciler, IValueNodePauseSeam
{
    /// <inheritdoc />
    /// <remarks>
    /// Answered by the gate itself, so it cannot claim more than the gate can do.
    /// <see cref="ValueRailStartup.GuardAccountPauseWired"/> reads it at boot: this seam is half
    /// of the pause, and a pause honoured by half the engine is not honoured.
    /// </remarks>
    public bool IsAccountPauseEnforced => _railGate.IsAccountPauseEnforced;

    private readonly IActionStore _actionStore;
    private readonly IActionRunStore _runStore;
    private readonly IActionNodeExecutionStore _executionStore;
    private readonly IActionAccessRequestStore _accessRequestStore;
    private readonly IActionDagValidator _dagValidator;
    private readonly IActionDagExecutabilityValidator _executabilityValidator;
    private readonly IActionNodeHandlerRegistry _registry;
    private readonly ISagaStore _sagaStore;
    private readonly ActionOperationRecovery _operationRecovery;
    private readonly IActionWorkflowAdmissionStore? _workflowAdmission;

    // D1/D9 pre-execution value-rail capability gate, and — since OI-28 — the account pause.
    // Constructed here (not injected) so the DI signature stays positional-compatible; the
    // pluggable halves (per-rail destination directories, the reversal ledger the pause reads)
    // ARE injected. See Services/Actions/AGENTS.md §rail-capability.
    private readonly ValueRailCapabilityGate _railGate;

    // Config-bound marketplace guards (see Managers/AGENTS.md §action-run-quota
    // and §economic-consent). Materialized once from IConfiguration at construction.
    private readonly ActionRunQuotaOptions _runQuota;

    // Semantic transition-legality layer (smart-gates-holon-state §8.2), invoked
    // ALONGSIDE the structural Kahn validator — never replacing it. Stateless with
    // the default project-lifecycle map; constructing it here keeps the DI signature
    // unchanged. A future track that needs a per-action lifecycle can promote this to
    // an injected dependency.
    private readonly ActionTransitionValidator _transitionValidator = new();

    // Parked-run sweep tuning. The BACKGROUND caller passes its own thresholds
    // (ReconciliationOptions, see Services/Reconciliation); these are the defaults the
    // synchronous operator endpoint sweeps with.
    private const int DefaultParkedRunHardStuckSeconds = 3600;
    private const int DefaultParkedRunBatchSize = 500;
    private const int MaxParkedRunBatchSize = 1000;

    public ActionManager(
        IActionStore actionStore,
        IActionRunStore runStore,
        IActionNodeExecutionStore executionStore,
        IActionDagValidator dagValidator,
        IActionDagExecutabilityValidator executabilityValidator,
        IActionNodeHandlerRegistry registry,
        ISagaStore sagaStore,
        IWalletManager walletManager,
        IBlockchainProviderFactory chainFactory,
        ActionConfigBindingResolver bindingResolver,
        // Optional so the ~10 unit-test fixtures that construct ActionManager with the
        // 10 positional deps keep compiling; DI always supplies the real config in
        // production. Null ⇒ default ActionRunQuotaOptions. See §action-run-quota.
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
        // Optional (like configuration) so existing positional test fixtures compile.
        // DI always supplies the real store; the invitation surface faults with a
        // clear message when it is absent. See §action-invitations.
        IActionAccessRequestStore? accessRequestStore = null,
        // Optional (like configuration) so existing positional test fixtures compile. DI
        // supplies every registered rail destination directory; with none registered the
        // gate falls back to the rail-scoped wallet directory. See §rail-capability.
        IEnumerable<IRailDestinationDirectory>? railDestinationDirectories = null,
        // OI-28: the read side of the account pause. Program.cs registers IFiatReversalLedger
        // unconditionally (AddFiatReversalPipeline), so production always supplies it; a container
        // without the reversal pipeline cannot hold a flag to honour.
        AZOA.WebAPI.Services.Fiat.Reversal.IFiatReversalLedger? accountPauseLedger = null,
        IActionWorkflowAdmissionStore? workflowAdmission = null,
        IEnumerable<IActionExternalOperationResolver>? operationResolvers = null)
    {
        _actionStore = actionStore;
        _runStore = runStore;
        _executionStore = executionStore;
        _workflowAdmission = workflowAdmission;
        _accessRequestStore = accessRequestStore!;
        _dagValidator = dagValidator;
        _executabilityValidator = executabilityValidator;
        _registry = registry;
        _sagaStore = sagaStore;
        _operationRecovery = new ActionOperationRecovery(operationResolvers ?? []);
        _runQuota = configuration is null
            ? new ActionRunQuotaOptions()
            : ActionRunQuotaOptions.FromConfiguration(configuration);
        _railGate = new ValueRailCapabilityGate(
            walletManager, chainFactory.DefaultRailId, railDestinationDirectories, accountPauseLedger);
    }

    /// <summary>
    /// Loads an action and rejects when it is owned by a different user. Returns
    /// the loaded action on success, or an error result to surface verbatim.
    /// </summary>
    private async Task<AZOAResult<ActionDefinition>> LoadOwnedActionAsync(Guid actionId, Guid userId)
    {
        var actionResult = await _actionStore.GetActionAsync(actionId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = "Action not found." };
        if (actionResult.Result.UserId != userId)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = OwnershipRefusal(actionResult.Result) };
        return actionResult;
    }

    // A private action the caller cannot read answers "not found", so an ownership
    // refusal is never an existence oracle. See Managers/AGENTS.md §action-read-scope.
    private static string OwnershipRefusal(ActionDefinition action) =>
        action.IsPublic ? "Action is owned by a different user." : "Action not found.";

    // Owner-or-public read rule shared by action/template/node-template surfaces.
    private static bool CanReadAuthored(Guid authorUserId, bool isPublic, Guid? callerUserId) =>
        isPublic || callerUserId.HasValue && authorUserId == callerUserId.Value;

    /// <summary>
    /// Loads an action for a run-start (marketplace mechanic): the OWNER may start
    /// any of their own actions; a NON-owner may start it only when it is published
    /// (<see cref="ActionDefinition.IsPublic"/>) AND Active. Returns the action and whether the
    /// caller is the owner, or an error result to surface verbatim. Private actions
    /// stay owner-only; a non-owner sees the same "not found"-style rejection to
    /// avoid leaking existence of private actions.
    /// See Managers/AGENTS.md §publish-lifecycle.
    /// </summary>
    private async Task<(AZOAResult<ActionDefinition> result, bool isOwner)> LoadStartableActionAsync(Guid actionId, Guid userId)
    {
        var actionResult = await _actionStore.GetActionAsync(actionId);
        if (actionResult.IsError || actionResult.Result == null)
            return (new AZOAResult<ActionDefinition> { IsError = true, Message = "Action not found." }, false);

        var action = actionResult.Result;
        if (action.UserId == userId)
            return (actionResult, true);

        // Non-owner path: only published (public + Active) actions are startable.
        if (!action.IsPublic)
            return (new AZOAResult<ActionDefinition> { IsError = true, Message = "Action not found." }, false);
        if (action.Status != ActionStatus.Active)
            return (new AZOAResult<ActionDefinition> { IsError = true, Message = "Action is not published for execution." }, false);

        // Run-authorization gate (action-invitations-approval): an InviteOnly action is
        // viewable by any non-owner (GetAsync unchanged) but startable/forkable only by
        // the owner + InvitedUserIds. See Managers/AGENTS.md §action-invitations.
        if (action.RunAccess == ActionRunAccess.InviteOnly && !action.InvitedUserIds.Contains(userId))
            return (new AZOAResult<ActionDefinition> { IsError = true, Message = "This action requires an invitation — request access." }, false);

        return (actionResult, false);
    }

    // ═══════════════════════════════════════════════════════════════════
    // MARKETPLACE RUN-START GUARDS (per-(user,action) quota + economic consent).
    // Both run-start seams (ExecuteAsync + StartWorkflowRunAsync) call these BEFORE
    // any run/exec rows are written — a breach rejects cleanly with no orphaned run.
    // See Managers/AGENTS.md §action-run-quota and §economic-consent.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-(user, action) run-start quota guard (treasury/runner drain). Rejects a
    /// new start when the user already holds the configured max NON-terminal runs
    /// of this action. Config-driven; owner gets a higher (or unbounded) ceiling.
    /// Returns an error result on breach, or null to proceed. See §action-run-quota.
    /// </summary>
    private async Task<AZOAResult<ActionRun>?> EnforceRunQuotaAsync(Guid actionId, Guid userId, bool isOwner)
    {
        var limit = _runQuota.EffectiveLimit(isOwner);
        if (limit is null)
            return null; // quota disabled / caller exempt

        // Count this user's non-terminal runs of THIS action from the run store.
        var runsResult = await _runStore.GetByActionIdAsync(actionId);
        if (runsResult.IsError)
            // Fail closed: we cannot prove the user is under quota, so reject.
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"Run-start quota check failed; run rejected (fail-closed): {runsResult.Message}"
            };

        var active = (runsResult.Result ?? Enumerable.Empty<ActionRun>())
            .Count(r => r.UserId == userId && !r.Status.IsTerminal());

        if (active >= limit.Value)
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"Run-start quota exceeded: user already has {active} active run(s) of action {actionId} " +
                          $"(limit {limit.Value}). Let existing runs reach a terminal state before starting another."
            };

        return null;
    }

    /// <summary>
    /// Economic-consent gate for a NON-owner (marketplace) run. Computes the
    /// value-moving-node manifest and, when any economic node is present, requires
    /// <paramref name="acknowledgeEconomicEffects"/> to be true. Owner runs are
    /// exempt (they authored the action). Returns an error on an un-acknowledged
    /// economic run, or null to proceed. See §economic-consent.
    /// </summary>
    private AZOAResult<ActionRun>? EnforceEconomicConsent(ActionDefinition action, bool isOwner, bool acknowledgeEconomicEffects)
    {
        if (isOwner)
            return null; // owner authored the action — no external disclosure needed

        var manifest = ActionEconomicManifestBuilder.Build(action, _registry, action.PublishedVersionHash);
        if (!manifest.HasEconomicNodes)
            return null; // nothing moves value — no consent required

        if (acknowledgeEconomicEffects)
            return null; // runner explicitly acknowledged the disclosed effects

        var summary = string.Join(", ", manifest.Entries.Select(e => $"{e.NodeType} '{e.NodeName}'"));
        return new AZOAResult<ActionRun>
        {
            IsError = true,
            Message = "This action moves assets on your behalf and requires explicit consent. " +
                      $"Value-moving nodes: [{summary}]. Preview them via the run-preview surface, " +
                      "then re-start with acknowledgeEconomicEffects=true to proceed."
        };
    }

    /// <summary>
    /// Loads a run and rejects when it is owned by a different user. Returns
    /// the loaded run on success, or an error result to surface verbatim.
    /// </summary>
    private async Task<AZOAResult<ActionRun>> LoadOwnedRunAsync(Guid runId, Guid userId)
    {
        var runResult = await _runStore.GetByIdAsync(runId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = "Action run not found." };
        // A run is never public: a foreign run answers exactly like a missing one.
        if (runResult.Result.UserId != userId)
            return new AZOAResult<ActionRun> { IsError = true, Message = "Action run not found." };
        return runResult;
    }

    // ═══════════════════════════════════════════════════════════════════
    // ACTION CRUD
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<ActionDefinition>> CreateAsync(ActionCreateModel model, Guid userId, AZOARequest? request = null)
    {
        var action = new ActionDefinition
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            Description = model.Description,
            UserId = userId,
            IsPublic = model.IsPublic,
            CreatedDate = DateTime.UtcNow
        };

        // Create nodes with new Ids
        var nodeIds = new List<Guid>();
        foreach (var nodeModel in model.Nodes)
        {
            var node = new ActionNode
            {
                Id = Guid.NewGuid(),
                ActionId = action.Id,
                Name = nodeModel.Name,
                NodeType = nodeModel.NodeType,
                Config = nodeModel.Config,
                IsEntry = nodeModel.IsEntry,
                IsTerminal = nodeModel.IsTerminal,
                NodeTemplateId = nodeModel.NodeTemplateId
            };
            action.Nodes.Add(node);
            nodeIds.Add(node.Id);
        }

        // Map edge indices to node Ids
        foreach (var edgeModel in model.Edges)
        {
            if (edgeModel.SourceNodeId < 0 || edgeModel.SourceNodeId >= nodeIds.Count ||
                edgeModel.TargetNodeId < 0 || edgeModel.TargetNodeId >= nodeIds.Count)
            {
                return new AZOAResult<ActionDefinition> { IsError = true, Message = $"Edge index out of range. Source={edgeModel.SourceNodeId}, Target={edgeModel.TargetNodeId}, NodeCount={nodeIds.Count}." };
            }

            var edge = new ActionEdge
            {
                Id = Guid.NewGuid(),
                ActionId = action.Id,
                SourceNodeId = nodeIds[edgeModel.SourceNodeId],
                TargetNodeId = nodeIds[edgeModel.TargetNodeId],
                Condition = edgeModel.Condition,
                EdgeType = edgeModel.EdgeType
            };
            action.Edges.Add(edge);
        }

        return await _actionStore.UpsertActionAsync(action);
    }

    public async Task<AZOAResult<ActionDefinition>> GetAsync(Guid id, Guid userId, AZOARequest? request = null)
    {
        // Owner may always read; a non-owner may read a PUBLIC action (read-only) so
        // it can be fetched and started under their own context. Private actions stay
        // owner-only and surface the same "different user" rejection.
        var actionResult = await _actionStore.GetActionAsync(id);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = "Action not found." };
        var action = actionResult.Result;
        if (action.UserId == userId || action.IsPublic)
            return actionResult;
        return new AZOAResult<ActionDefinition> { IsError = true, Message = "Action not found." };
    }

    /// <summary>
    /// Marketplace discovery: actions any user may fork/start — public AND published
    /// (Active). The browse surface for the start-as-template mechanic; without it a
    /// public action is only reachable if its id was shared out-of-band. Mirrors the
    /// non-owner gate in <see cref="LoadStartableActionAsync"/> (IsPublic && Active).
    /// </summary>
    public async Task<AZOAResult<IEnumerable<ActionDefinition>>> ListPublicAsync(AZOARequest? request = null)
    {
        var all = await _actionStore.GetPublicActionsAsync();
        if (all.IsError || all.Result == null) return all;
        var published = all.Result.Where(q => q.Status == ActionStatus.Active);
        return new AZOAResult<IEnumerable<ActionDefinition>> { Result = published, Message = "Public actions." };
    }

    public async Task<AZOAResult<IEnumerable<ActionDefinition>>> GetByUserAsync(Guid userId, AZOARequest? request = null)
    {
        return await _actionStore.GetActionsByUserAsync(userId);
    }

    public async Task<AZOAResult<ActionDefinition>> UpdateAsync(Guid id, ActionUpdateModel model, Guid userId, AZOARequest? request = null)
    {
        var existing = await LoadOwnedActionAsync(id, userId);
        if (existing.IsError || existing.Result == null) return existing;

        var action = existing.Result;
        if (model.Name != null) action.Name = model.Name;
        if (model.Description != null) action.Description = model.Description;
        // Owner-only visibility toggle (LoadOwnedActionAsync above already enforced
        // ownership → IDOR-safe). Null leaves the flag unchanged.
        if (model.IsPublic.HasValue) action.IsPublic = model.IsPublic.Value;
        // model.Status is intentionally ignored — runtime status moved to
        // ActionRun.Status (see ADR §2.2). The field on ActionUpdateModel is
        // retained for API back-compat but has no effect on the definition.

        return await _actionStore.UpsertActionAsync(action);
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid userId, AZOARequest? request = null)
    {
        var owned = await LoadOwnedActionAsync(id, userId);
        if (owned.IsError || owned.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = owned.Message };

        return await _actionStore.DeleteActionAsync(id);
    }

    // ═══════════════════════════════════════════════════════════════════
    // DEFINITION LIFECYCLE — publish / unpublish (FR-2)
    // See Managers/AGENTS.md §publish-lifecycle.
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<ActionDefinition>> PublishAsync(Guid actionId, Guid userId, AZOARequest? request = null)
    {
        var owned = await LoadOwnedActionAsync(actionId, userId);
        if (owned.IsError || owned.Result == null)
            return owned;
        var action = owned.Result;

        if (action.Status == ActionStatus.Active)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = "Action is already Active." };

        // A not-enabled node type may not become runnable. Publish refuses so a broken

        // primitive cannot enter an Active action; the dispatch seams refuse too, so an

        // already-published action cannot run one either.

        var blockedNode = action.Nodes?.FirstOrDefault(

            n => Services.Actions.NotEnabledActionNodeTypes.IsBlocked(n.NodeType));

        if (blockedNode != null)

            return new AZOAResult<ActionDefinition>

            {

                IsError = true,

                Message = "Publish failed — " + Services.Actions.NotEnabledActionNodeTypes

                    .RefusalMessage(blockedNode.NodeType)

            };


        // Publish uses the engine profile: fan-out is an error here (Managers/AGENTS.md §publish-lifecycle).
        var dagResult = _dagValidator.Validate(action, fanOutAsError: true);
        if (!dagResult.IsValid)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = $"Publish failed — DAG invalid: {string.Join("; ", dagResult.Errors)}" };

        var transitionResult = _transitionValidator.Validate(action);
        if (!transitionResult.IsValid)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = $"Publish failed — transition invalid: {string.Join("; ", transitionResult.Errors)}" };

        // Per-node config check (hook filled in by Phase C / ActionNodeConfigRegistry).
        var configError = ValidateNodeConfigs(action);
        if (configError != null)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = $"Publish failed — node config invalid: {configError}" };

        // Executability gate: reject if any $from binding input won't be satisfiable
        // at runtime (unreachable source, absent output field, provable scalar type
        // mismatch). Runs LAST so structural DAG / transition / config errors surface
        // first. See Services/Actions/AGENTS.md §executability-validation.
        var execResult = _executabilityValidator.Validate(action);
        if (!execResult.IsValid)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = $"Publish failed — binding not executable: {string.Join("; ", execResult.Errors)}" };

        // F6 TOCTOU guard: flip Draft → Active via compare-and-swap on the version
        // we read above. This is the serialization point — two concurrent publishes
        // race here and exactly one wins; the loser sees affected==0 (the row already
        // advanced) and gets a conflict, never a torn double-publish.
        var expectedVersion = action.Version;
        var won = await _actionStore.TryTransitionActionStatusAsync(
            actionId, ActionStatus.Draft, ActionStatus.Active, expectedVersion);
        if (won == 0)
            return new AZOAResult<ActionDefinition>
            {
                IsError = true,
                Message = "Publish conflict: the action was modified or published concurrently. Reload and retry."
            };

        // Persist fresh ExecutionOrder (dagResult already populated it via Validate).
        // Safe now: we own the Active transition, so no concurrent publisher can
        // interleave a graph write. Assign the authoritative post-CAS row shape
        // (status Active, version bumped once) so the CONTENT upsert round-trips it
        // exactly — assignment (not +=) is reference-sharing-safe for the in-memory
        // store, whose CAS mutated the same instance.
        action.Status  = ActionStatus.Active;
        action.Version = expectedVersion + 1;
        // Bait-and-switch guard: snapshot a stable content hash of the node/edge
        // graph being published. Runs bind to this exact revision; a later
        // unpublish→edit→republish recomputes it, so a mismatch is detectable.
        // See Managers/AGENTS.md §published-version-hash.
        action.PublishedVersionHash = ActionPublishedVersion.ComputeHash(action);
        var upsert = await _actionStore.UpsertActionAsync(action);
        if (upsert.IsError)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = upsert.Message };

        return new AZOAResult<ActionDefinition> { Result = upsert.Result, Message = "Action published." };
    }

    public async Task<AZOAResult<ActionDefinition>> UnpublishAsync(Guid actionId, Guid userId, AZOARequest? request = null)
    {
        var owned = await LoadOwnedActionAsync(actionId, userId);
        if (owned.IsError || owned.Result == null)
            return owned;
        var action = owned.Result;

        if (action.Status != ActionStatus.Active)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = "Action is not Active; cannot unpublish." };

        // Refuse while any in-flight run exists (AC-2d).
        var runsResult = await _runStore.GetByActionIdAsync(actionId);
        if (!runsResult.IsError && runsResult.Result != null)
        {
            var inFlight = runsResult.Result.Where(r => !r.Status.IsTerminal()).ToList();
            if (inFlight.Count > 0)
                return new AZOAResult<ActionDefinition>
                {
                    IsError = true,
                    Message = $"Cannot unpublish: {inFlight.Count} in-flight run(s) exist. Wait for them to reach a terminal state."
                };
        }

        // F6 TOCTOU guard: flip Active → Draft via compare-and-swap on the version
        // we read above. If a concurrent publish/unpublish moved the row (version
        // changed) the CAS misses and we return conflict rather than clobbering.
        // Together with the run-start version-confirm, this closes unpublish racing
        // a run-start: whichever transition commits first bumps version, so the
        // other observes the change.
        var expectedVersion = action.Version;
        var won = await _actionStore.TryTransitionActionStatusAsync(
            actionId, ActionStatus.Active, ActionStatus.Draft, expectedVersion);
        if (won == 0)
            return new AZOAResult<ActionDefinition>
            {
                IsError = true,
                Message = "Unpublish conflict: the action was modified or transitioned concurrently. Reload and retry."
            };

        // Reflect the authoritative post-CAS shape (assignment, not +=, is
        // reference-sharing-safe for the in-memory store's in-place CAS).
        action.Status  = ActionStatus.Draft;
        action.Version = expectedVersion + 1;
        return new AZOAResult<ActionDefinition> { Result = action, Message = "Action unpublished." };
    }

    /// <summary>
    /// Per-node config validation at publish time: runs the binding pre-pass
    /// with the node's direct upstream names (so upstream.&lt;name&gt; paths are
    /// checked against the actual incoming edges) plus the strict shadow round-trip.
    /// Returns the first error message, or null when all nodes pass.
    /// </summary>
    private string? ValidateNodeConfigs(ActionDefinition action)
    {
        // run.<name> is run-scoped (any node in the action), not edge-scoped, so
        // it validates against the full node-name set rather than direct upstreams.
        var allNodeNames = action.Nodes.Select(n => n.Name).ToHashSet();

        foreach (var node in action.Nodes)
        {
            // Build the set of direct upstream node names (sources of incoming edges).
            var directUpstreamNames = action.Edges
                .Where(e => e.TargetNodeId == node.Id)
                .Select(e => action.Nodes.FirstOrDefault(n => n.Id == e.SourceNodeId)?.Name)
                .Where(n => n is not null)
                .Cast<string>()
                .ToHashSet();

            var err = ActionNodeConfigRegistry.Validate(node.NodeType, node.Config, directUpstreamNames, allNodeNames);
            if (err != null)
                return $"Node '{node.Name}' ({node.NodeType}): {err}";
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    // DAG VALIDATION
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> ValidateDAGAsync(Guid actionId, Guid userId, AZOARequest? request = null)
    {
        // The HTTP route validates (and persists ExecutionOrder on) the caller's OWN action only.
        var owned = await LoadOwnedActionAsync(actionId, userId);
        if (owned.IsError || owned.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = owned.Message };
        return await ValidateDAGAsync(actionId, request);
    }

    public async Task<AZOAResult<bool>> ValidateDAGAsync(Guid actionId, AZOARequest? request = null)
    {
        var actionResult = await _actionStore.GetActionAsync(actionId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = actionResult.Message };

        var action = actionResult.Result;

        // ActionDagValidator is the single ExecutionOrder authority — Validate
        // mutates node.ExecutionOrder in-place on the action graph.
        var validation = _dagValidator.Validate(action);

        if (!validation.IsValid)
        {
            return new AZOAResult<bool>
            {
                IsError = true,
                Result = false,
                Message = $"DAG validation failed: {string.Join("; ", validation.Errors)}"
            };
        }

        // ADDED semantic layer (smart-gates-holon-state §8.2): structural validity
        // does not prove that a gated edge encoding a phase transition encodes a
        // LEGAL one. Run the transition validator alongside the Kahn pass and reject
        // an authored DAG whose phase-transition edges jump illegally
        // (e.g. DRAFT -> IN_PROGRESS without FUNDED). Edges that do not encode a phase
        // transition are ignored by this layer.
        var transitionValidation = _transitionValidator.Validate(action);
        if (!transitionValidation.IsValid)
        {
            return new AZOAResult<bool>
            {
                IsError = true,
                Result = false,
                Message = $"DAG validation failed: {string.Join("; ", transitionValidation.Errors)}"
            };
        }

        await _actionStore.UpsertActionAsync(action);

        return new AZOAResult<bool> { Result = true, Message = "DAG is valid." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // EXECUTION
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public Task<AZOAResult<ActionRun>> ExecuteAsync(Guid actionId, Guid userId, AZOARequest? request = null,
        Guid? actingTenantId = null, bool acknowledgeEconomicEffects = false)
        => StartWorkflowRunAsync(actionId, userId, request, actingTenantId, acknowledgeEconomicEffects);

    /// <inheritdoc/>
    public async Task<AZOAResult<ActionNodeExecution>> ExecuteNodeAsync(Guid actionId, Guid nodeId, Guid userId,
        AZOARequest? request = null, Guid? actingTenantId = null)
    {
        var loaded = await LoadOwnedActionAsync(actionId, userId);
        if (loaded.IsError || loaded.Result is null) return AZOAResult<ActionNodeExecution>.Failure(loaded.Message);
        var source = loaded.Result;
        if (source.Status != ActionStatus.Active)
            return AZOAResult<ActionNodeExecution>.Failure("Publish the action before admitting node execution.");
        var node = source.Nodes.SingleOrDefault(item => item.Id == nodeId);
        if (node is null) return AZOAResult<ActionNodeExecution>.Failure("Node not found.");
        var quota = await EnforceRunQuotaAsync(actionId, userId, true);
        if (quota is not null) return AZOAResult<ActionNodeExecution>.Failure(quota.Message);
        var dependencies = await EnforceDependenciesAsync(source, userId, request);
        if (dependencies is not null) return AZOAResult<ActionNodeExecution>.Failure(dependencies.Message);
        var graph = System.Text.Json.JsonSerializer.Deserialize<ActionDefinition>(System.Text.Json.JsonSerializer.Serialize(source))
            ?? throw new InvalidOperationException("Action snapshot is unreadable.");
        graph.Nodes = graph.Nodes.Where(item => item.Id == nodeId).ToList();
        graph.Edges = [];
        var validation = _executabilityValidator.Validate(graph);
        var configError = ValidateNodeConfigs(graph);
        if (!validation.IsValid || configError is not null)
            return AZOAResult<ActionNodeExecution>.Failure(configError ?? string.Join("; ", validation.Errors));
        if (NotEnabledActionNodeTypes.IsBlocked(node.NodeType))
            return AZOAResult<ActionNodeExecution>.Failure(NotEnabledActionNodeTypes.RefusalMessage(node.NodeType));
        var run = new ActionRun { Id = Guid.NewGuid(), ActionId = actionId, UserId = userId,
            ActingTenantId = actingTenantId, Status = ActionRunStatus.Running, StartedAt = DateTime.UtcNow,
            PublishedVersionHash = source.PublishedVersionHash };
        ActionRunSnapshot.Capture(run, graph);
        var admission = _workflowAdmission ?? throw new InvalidOperationException("Atomic workflow admission is not registered.");
        var admitted = await admission.AdmitAsync(new ActionWorkflowAdmission(run, graph,
            [new ActionSourceAdmission(source.Id, source.Version, source.PublishedVersionHash, _runQuota.EffectiveLimit(true))], nodeId));
        if (admitted.IsError) return AZOAResult<ActionNodeExecution>.Failure(admitted.Message);
        var execution = await _executionStore.GetByRunAndNodeAsync(run.Id, nodeId);
        if (!execution.IsError) execution.Message = "Node admitted; use its run ID to observe durable execution.";
        return execution;
    }

    public async Task<AZOAResult<ActionRun>> ForkAsync(Guid runId, Guid atNodeId, string reason, Guid userId, AZOARequest? request = null)
    {
        var parentResult = await LoadOwnedRunAsync(runId, userId);
        if (parentResult.IsError || parentResult.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = parentResult.Message };

        var parent = parentResult.Result;

        // State-machine guard: only Running runs are forkable. Succeeded runs
        // are re-runnable (new root) but not forkable; Failed/Forked/Cancelled
        // are terminal. See ADR §2.3.
        if (parent.Status != ActionRunStatus.Running)
        {
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"Cannot fork run {runId}: status is {parent.Status} (only Running runs are forkable)."
            };
        }

        var actionResult = await LoadStartableActionAsync(parent.ActionId, userId);
        if (actionResult.Item1.IsError || actionResult.Item1.Result is null)
            return AZOAResult<ActionRun>.Failure(actionResult.Item1.Message);
        if (parent.DefinitionSnapshotJson is null)
            return AZOAResult<ActionRun>.Failure("Legacy run has no frozen definition to fork safely.");
        var action = ActionRunSnapshot.Read(parent);
        if (actionResult.Item1.Result.Version != action.Version
            || actionResult.Item1.Result.PublishedVersionHash != action.PublishedVersionHash)
            return AZOAResult<ActionRun>.Failure("The action changed after the parent run was admitted; start a new run with current consent.");
        var forkPointNode = action.Nodes.FirstOrDefault(n => n.Id == atNodeId);
        if (forkPointNode == null)
        {
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"Cannot fork at node {atNodeId}: not present in action {parent.ActionId} definition."
            };
        }
        var forkPoint = forkPointNode.ExecutionOrder;

        // A fork mints a NEW non-terminal run, so it counts against the per-(user,
        // action) quota — otherwise fork is an unbounded run-multiplication bypass of
        // the treasury/drain guard the run-start seams enforce (see Managers/AGENTS.md
        // §action-run-quota). Consent is NOT re-checked: the parent run already
        // disclosed + acknowledged the economic manifest at start, and the fork
        // inherits that same action revision.
        var forkQuotaGate = await EnforceRunQuotaAsync(parent.ActionId, userId, isOwner: parent.UserId == action.UserId);
        if (forkQuotaGate != null) return forkQuotaGate;

        // Run-authorization gate (action-invitations-approval): a fork RE-RUNS the
        // action's nodes, so it is gated exactly like a fresh start. A non-owner runner
        // on an InviteOnly action who is no longer invited cannot fork. (An in-flight run
        // that has already started is unaffected — only NEW starts/forks are gated;
        // see Managers/AGENTS.md §action-invitations.)
        if (parent.UserId != action.UserId
            && action.RunAccess == ActionRunAccess.InviteOnly
            && !action.InvitedUserIds.Contains(parent.UserId))
        {
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = "This action requires an invitation — request access."
            };
        }

        // Create the child run with lineage fields populated.
        var child = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = parent.ActionId,
            UserId = parent.UserId,
            // tenant-consent-delegation AC4: a tenant-driven run stays tenant-driven
            // across forks — inherit the parent run's acting tenant (null = user-driven)
            // rather than re-reading a principal, since the fork has no fresh JWT context.
            ActingTenantId = parent.ActingTenantId,
            Status = ActionRunStatus.Pending,
            StartedAt = DateTime.UtcNow,
            ParentRunId = parent.Id,
            ForkedAtNodeId = atNodeId,
            ForkReason = reason,
            DefinitionSnapshotJson = parent.DefinitionSnapshotJson,
            DefinitionSnapshotHash = parent.DefinitionSnapshotHash,
            InternalDependenciesJson = parent.InternalDependenciesJson,
            OccurrenceBoundariesJson = parent.OccurrenceBoundariesJson,
            InitialInputJson = parent.InitialInputJson,
        };
        var createChild = await _runStore.CreateAsync(child);
        if (createChild.IsError || createChild.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = createChild.Message };

        // Copy-by-reference: for nodes with ExecutionOrder < forkPoint, the
        // parent's execution row is shared with the child. The InMemory
        // implementation does this by creating a new (childRunId, nodeId)
        // row that mirrors the parent's execution state (same Id, same
        // Output/Error/State). The SurrealDB write-through (surrealdb-migration
        // tasks 9–10) materializes this as a `RELATE action_run -> executes ->
        // action_node_execution` edge — no duplication. See SURREAL-SCHEMA-HINTS §6.2.
        var parentExecs = (await _executionStore.GetByRunIdAsync(parent.Id)).Result
            ?? Enumerable.Empty<ActionNodeExecution>();
        foreach (var parentExec in parentExecs)
        {
            var parentNode = action.Nodes.FirstOrDefault(n => n.Id == parentExec.NodeId);
            if (parentNode == null) continue;
            if (parentNode.ExecutionOrder >= forkPoint) continue;

            // Mirror the parent's completed execution onto the child run.
            var mirror = new ActionNodeExecution
            {
                Id = Guid.NewGuid(),
                RunId = child.Id,
                NodeId = parentExec.NodeId,
                State = parentExec.State,
                Output = parentExec.Output,
                Error = parentExec.Error,
                StartedAt = parentExec.StartedAt,
                EndedAt = parentExec.EndedAt
            };
            await _executionStore.CreateAsync(mirror);
        }

        // Cancel parent's in-flight (Pending / Running) node executions.
        // HIGH#7 — the state-machine guard prevents a late-arriving terminal
        // transition (e.g. a node that succeeded between our GetByRunId snap
        // and the cancel write) from being silently overwritten by Cancelled.
        // We pass the freshly-observed state as the guard; if the store
        // returns an error, the row already moved past the in-flight window
        // and we accept its terminal state (no resurrection).
        foreach (var pe in parentExecs)
        {
            if (pe.State != ActionNodeState.Pending && pe.State != ActionNodeState.Running) continue;
            var observedState = pe.State;
            pe.State = ActionNodeState.Cancelled;
            pe.EndedAt = DateTime.UtcNow;
            await _executionStore.UpdateAsync(pe, expectedState: observedState);
        }

        // Transition parent Running → Forked (terminal).
        parent.Status = ActionRunStatus.Forked;
        parent.EndedAt = DateTime.UtcNow;
        await _runStore.UpdateAsync(parent);

        return new AZOAResult<ActionRun> { Result = child, Message = "Fork created." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // SUPERVISOR-DRIVEN FAIL
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<ActionRun>> MarkRunFailedAsync(Guid runId, string reason, Guid userId, AZOARequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, userId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = runResult.Message };

        var run = runResult.Result;
        if (run.Status != ActionRunStatus.Running)
        {
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"Cannot mark run {runId} failed: status is {run.Status} (only Running runs accept supervisor fail)."
            };
        }

        // Cancel any in-flight node executions (same shape as fork).
        // HIGH#7 — same state-machine guard as ForkAsync (line ~509).
        var execs = (await _executionStore.GetByRunIdAsync(run.Id)).Result
            ?? Enumerable.Empty<ActionNodeExecution>();
        foreach (var exec in execs)
        {
            if (exec.State != ActionNodeState.Pending && exec.State != ActionNodeState.Running) continue;
            var observedState = exec.State;
            exec.State = ActionNodeState.Cancelled;
            exec.EndedAt = DateTime.UtcNow;
            await _executionStore.UpdateAsync(exec, expectedState: observedState);
        }

        run.Status = ActionRunStatus.Failed;
        run.FailReason = reason;
        run.EndedAt = DateTime.UtcNow;
        var updated = await _runStore.UpdateAsync(run);

        return new AZOAResult<ActionRun> { Result = updated.Result, Message = $"Run marked failed: {reason}" };
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<ActionTemplate>> CreateTemplateAsync(ActionTemplateCreateModel model, Guid userId, AZOARequest? request = null)
    {
        var template = new ActionTemplate
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            Description = model.Description,
            AuthorUserId = userId,
            Parameters = model.Parameters,
            Version = model.Version,
            IsPublic = model.IsPublic,
            Tags = model.Tags
        };

        // Build template nodes from the create model
        var slotIds = new List<string>();
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            var nodeModel = model.Nodes[i];
            var slotId = $"slot_{i}";
            slotIds.Add(slotId);

            template.Nodes.Add(new ActionTemplateNode
            {
                Id = Guid.NewGuid(),
                TemplateId = template.Id,
                SlotId = slotId,
                NodeTemplateId = nodeModel.NodeTemplateId ?? Guid.Empty,
                ParamOverrides = nodeModel.Config,
                IsEntry = nodeModel.IsEntry,
                IsTerminal = nodeModel.IsTerminal
            });
        }

        foreach (var edgeModel in model.Edges)
        {
            if (edgeModel.SourceNodeId < 0 || edgeModel.SourceNodeId >= slotIds.Count ||
                edgeModel.TargetNodeId < 0 || edgeModel.TargetNodeId >= slotIds.Count)
            {
                return new AZOAResult<ActionTemplate> { IsError = true, Message = "Edge index out of range." };
            }

            template.Edges.Add(new ActionTemplateEdge
            {
                Id = Guid.NewGuid(),
                TemplateId = template.Id,
                SourceSlotId = slotIds[edgeModel.SourceNodeId],
                TargetSlotId = slotIds[edgeModel.TargetNodeId],
                EdgeType = edgeModel.EdgeType
            });
        }

        return await _actionStore.UpsertActionTemplateAsync(template);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ActionTemplate>> GetTemplateAsync(Guid id, Guid? callerUserId, AZOARequest? request = null)
    {
        var template = await _actionStore.GetActionTemplateAsync(id);
        return template.IsError || template.Result == null
            || !CanReadAuthored(template.Result.AuthorUserId, template.Result.IsPublic, callerUserId)
            ? AZOAResult<ActionTemplate>.Failure("Action template not found.")
            : template;
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<ActionTemplate>>> ListTemplatesAsync(Guid? callerUserId, AZOARequest? request = null)
    {
        var all = await _actionStore.GetAllActionTemplatesAsync();
        if (all.IsError || all.Result == null) return all;
        return new AZOAResult<IEnumerable<ActionTemplate>>
        {
            Result = all.Result.Where(t => CanReadAuthored(t.AuthorUserId, t.IsPublic, callerUserId)).ToList(),
            Message = all.Message
        };
    }

    public async Task<AZOAResult<ActionDefinition>> InstantiateTemplateAsync(Guid templateId, Guid userId, Dictionary<string, string>? parameters = null, AZOARequest? request = null)
    {
        var templateResult = await GetTemplateAsync(templateId, userId, request);
        if (templateResult.IsError || templateResult.Result == null)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = templateResult.Message };

        var template = templateResult.Result;

        // Node templates resolve as the TEMPLATE AUTHOR could read them: a public template
        // cannot be used to lift another author's private node-template config.
        var nodeTemplatesResult = await _actionStore.GetAllActionNodeTemplatesAsync();
        var nodeTemplates = (nodeTemplatesResult.Result ?? Enumerable.Empty<ActionNodeTemplate>())
            .Where(nt => CanReadAuthored(nt.AuthorUserId, nt.IsPublic, template.AuthorUserId))
            .ToDictionary(nt => nt.Id);

        var action = new ActionDefinition
        {
            Id = Guid.NewGuid(),
            Name = template.Name,
            Description = template.Description,
            UserId = userId,
            TemplateId = templateId,
            // Denormalise origin onto the copy: the template's author is the creator
            // this instantiated action descends from (marketplace provenance).
            OriginUserId = template.AuthorUserId,
            CreatedDate = DateTime.UtcNow
        };

        // Map slotId -> new node Id
        var slotToNodeId = new Dictionary<string, Guid>();

        foreach (var templateNode in template.Nodes)
        {
            var nodeId = Guid.NewGuid();
            slotToNodeId[templateNode.SlotId] = nodeId;

            // Resolve config from node template with param overrides
            var config = templateNode.ParamOverrides;
            if (nodeTemplates.TryGetValue(templateNode.NodeTemplateId, out var nodeTemplate))
            {
                config = string.IsNullOrEmpty(templateNode.ParamOverrides) || templateNode.ParamOverrides == "{}"
                    ? nodeTemplate.DefaultConfig
                    : templateNode.ParamOverrides;
            }

            // Apply parameter substitutions
            if (parameters != null)
            {
                foreach (var (key, value) in parameters)
                    config = config.Replace($"{{{{{key}}}}}", value);
            }

            var nodeType = nodeTemplate?.NodeType ?? ActionNodeType.HolonGet;

            action.Nodes.Add(new ActionNode
            {
                Id = nodeId,
                ActionId = action.Id,
                NodeTemplateId = templateNode.NodeTemplateId,
                NodeType = nodeType,
                Name = nodeTemplate?.Name ?? templateNode.SlotId,
                Config = config,
                IsEntry = templateNode.IsEntry,
                IsTerminal = templateNode.IsTerminal
            });
        }

        foreach (var templateEdge in template.Edges)
        {
            if (!slotToNodeId.TryGetValue(templateEdge.SourceSlotId, out var sourceId) ||
                !slotToNodeId.TryGetValue(templateEdge.TargetSlotId, out var targetId))
                continue;

            action.Edges.Add(new ActionEdge
            {
                Id = Guid.NewGuid(),
                ActionId = action.Id,
                SourceNodeId = sourceId,
                TargetNodeId = targetId,
                EdgeType = templateEdge.EdgeType
            });
        }

        return await _actionStore.UpsertActionAsync(action);
    }

    // ═══════════════════════════════════════════════════════════════════
    // NODE TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<ActionNodeTemplate>> CreateNodeTemplateAsync(ActionNodeTemplateCreateModel model, Guid userId, AZOARequest? request = null)
    {
        var template = new ActionNodeTemplate
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            NodeType = model.NodeType,
            Description = model.Description,
            DefaultConfig = model.DefaultConfig,
            ConfigSchema = model.ConfigSchema,
            InputSchema = model.InputSchema,
            OutputSchema = model.OutputSchema,
            Version = model.Version,
            AuthorUserId = userId,
            IsPublic = model.IsPublic,
            Tags = model.Tags
        };

        return await _actionStore.UpsertActionNodeTemplateAsync(template);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<ActionNodeTemplate>>> ListNodeTemplatesAsync(Guid? callerUserId, AZOARequest? request = null)
    {
        var all = await _actionStore.GetAllActionNodeTemplatesAsync();
        if (all.IsError || all.Result == null) return all;
        return new AZOAResult<IEnumerable<ActionNodeTemplate>>
        {
            Result = all.Result.Where(t => CanReadAuthored(t.AuthorUserId, t.IsPublic, callerUserId)).ToList(),
            Message = all.Message
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // ACTION NODES SUB-RESOURCE (post-hoc edits on a persisted action)
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<IEnumerable<ActionNode>>> ListNodesAsync(Guid actionId, Guid userId, AZOARequest? request = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, userId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<IEnumerable<ActionNode>> { IsError = true, Message = actionResult.Message };

        return new AZOAResult<IEnumerable<ActionNode>>
        {
            Result = actionResult.Result.Nodes,
            Message = "Success"
        };
    }

    public async Task<AZOAResult<ActionNode>> AddNodeAsync(Guid actionId, ActionNodeCreateModel model, Guid userId, AZOARequest? request = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, userId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionNode> { IsError = true, Message = actionResult.Message };

        var action = actionResult.Result;
        // FR-2 / AC-2c: mutations are blocked while the action is Active.
        if (action.Status == ActionStatus.Active)
            return new AZOAResult<ActionNode> { IsError = true, Message = "Cannot mutate an Active action. Call POST /{id}/unpublish first." };

        // FR-4 / AC-4b: validate config schema before persisting.
        var configValidationError = ActionNodeConfigRegistry.Validate(model.NodeType, model.Config);
        if (configValidationError != null)
            return new AZOAResult<ActionNode> { IsError = true, Message = $"Node config error: {configValidationError}" };

        var node = new ActionNode
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            Name = model.Name,
            NodeType = model.NodeType,
            Config = model.Config,
            IsEntry = model.IsEntry,
            IsTerminal = model.IsTerminal,
            NodeTemplateId = model.NodeTemplateId
        };
        action.Nodes.Add(node);

        var upsert = await _actionStore.UpsertActionAsync(action);
        if (upsert.IsError)
            return new AZOAResult<ActionNode> { IsError = true, Message = upsert.Message };

        return new AZOAResult<ActionNode> { Result = node, Message = "Node added." };
    }

    public async Task<AZOAResult<ActionNode>> UpdateNodeAsync(Guid actionId, Guid nodeId, ActionNodeUpdateModel model, Guid userId, AZOARequest? request = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, userId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionNode> { IsError = true, Message = actionResult.Message };

        var action = actionResult.Result;
        // FR-2 / AC-2c: mutations are blocked while the action is Active.
        if (action.Status == ActionStatus.Active)
            return new AZOAResult<ActionNode> { IsError = true, Message = "Cannot mutate an Active action. Call POST /{id}/unpublish first." };
        var node = action.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return new AZOAResult<ActionNode> { IsError = true, Message = $"Node {nodeId} not found in action {actionId}." };

        // FR-4 / AC-4b: validate new config schema before applying.
        if (model.Config != null)
        {
            var configValidationError = ActionNodeConfigRegistry.Validate(node.NodeType, model.Config);
            if (configValidationError != null)
                return new AZOAResult<ActionNode> { IsError = true, Message = $"Node config error: {configValidationError}" };
        }

        // Patch semantics: only non-null fields are applied.
        if (model.Name != null) node.Name = model.Name;
        if (model.Config != null) node.Config = model.Config;
        if (model.IsEntry.HasValue) node.IsEntry = model.IsEntry.Value;
        if (model.IsTerminal.HasValue) node.IsTerminal = model.IsTerminal.Value;

        var upsert = await _actionStore.UpsertActionAsync(action);
        if (upsert.IsError)
            return new AZOAResult<ActionNode> { IsError = true, Message = upsert.Message };

        return new AZOAResult<ActionNode> { Result = node, Message = "Node updated." };
    }

    public async Task<AZOAResult<bool>> DeleteNodeAsync(Guid actionId, Guid nodeId, Guid userId, AZOARequest? request = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, userId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = actionResult.Message };

        var action = actionResult.Result;
        // FR-2 / AC-2c: mutations are blocked while the action is Active.
        if (action.Status == ActionStatus.Active)
            return new AZOAResult<bool> { IsError = true, Message = "Cannot mutate an Active action. Call POST /{id}/unpublish first." };
        var node = action.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return new AZOAResult<bool> { IsError = true, Message = $"Node {nodeId} not found in action {actionId}." };

        // Reject if node has any edges referencing it — orphaning edges would
        // produce an invalid DAG. Callers must clear edges first via
        // RemoveEdgeAsync. This preserves the graph invariant that every edge
        // endpoint references an existing node.
        var referencingEdges = action.Edges
            .Where(e => e.SourceNodeId == nodeId || e.TargetNodeId == nodeId)
            .ToList();
        if (referencingEdges.Count > 0)
        {
            return new AZOAResult<bool>
            {
                IsError = true,
                Result = false,
                Message = $"Cannot delete node {nodeId}: {referencingEdges.Count} edge(s) reference it. Remove the referencing edges first."
            };
        }

        action.Nodes.Remove(node);
        var upsert = await _actionStore.UpsertActionAsync(action);
        if (upsert.IsError)
            return new AZOAResult<bool> { IsError = true, Message = upsert.Message };

        return new AZOAResult<bool> { Result = true, Message = "Node deleted." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // ACTION EDGES SUB-RESOURCE
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<ActionEdge>> AddEdgeAsync(Guid actionId, ActionEdgeAddModel model, Guid userId, AZOARequest? request = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, userId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionEdge> { IsError = true, Message = actionResult.Message };

        var action = actionResult.Result;
        // FR-2 / AC-2c: mutations are blocked while the action is Active.
        if (action.Status == ActionStatus.Active)
            return new AZOAResult<ActionEdge> { IsError = true, Message = "Cannot mutate an Active action. Call POST /{id}/unpublish first." };

        // Endpoint existence guard — both ends must reference nodes already in
        // the action. Without this an invalid graph can be persisted and only
        // discovered at validate/execute time.
        if (action.Nodes.All(n => n.Id != model.SourceNodeId))
            return new AZOAResult<ActionEdge> { IsError = true, Message = $"SourceNodeId {model.SourceNodeId} is not present in action {actionId}." };
        if (action.Nodes.All(n => n.Id != model.TargetNodeId))
            return new AZOAResult<ActionEdge> { IsError = true, Message = $"TargetNodeId {model.TargetNodeId} is not present in action {actionId}." };
        if (model.SourceNodeId == model.TargetNodeId)
            return new AZOAResult<ActionEdge> { IsError = true, Message = "SourceNodeId and TargetNodeId must not be the same node (self-loops not allowed)." };

        var edge = new ActionEdge
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            SourceNodeId = model.SourceNodeId,
            TargetNodeId = model.TargetNodeId,
            Condition = model.Condition,
            EdgeType = model.EdgeType
        };
        action.Edges.Add(edge);

        // Cycle guard: run the DAG validator after staging the new edge and
        // reject only on cycle-class errors. We deliberately ignore the
        // entry/terminal/orphan checks because an action is editable in-flight
        // — terminal/entry flags and full reachability are only required
        // immediately before ExecuteAsync (which runs the full ValidateDAGAsync).
        // Allowing orphan-class errors here would prevent any incremental
        // edge wiring on a partially-built graph.
        var validation = _dagValidator.Validate(action);
        var cycleErrors = validation.Errors
            .Where(e => e.Contains("Cycle detected", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (cycleErrors.Count > 0)
        {
            action.Edges.Remove(edge);
            return new AZOAResult<ActionEdge>
            {
                IsError = true,
                Message = $"Edge would invalidate DAG: {string.Join("; ", cycleErrors)}"
            };
        }

        var upsert = await _actionStore.UpsertActionAsync(action);
        if (upsert.IsError)
            return new AZOAResult<ActionEdge> { IsError = true, Message = upsert.Message };

        return new AZOAResult<ActionEdge> { Result = edge, Message = "Edge added." };
    }

    public async Task<AZOAResult<bool>> RemoveEdgeAsync(Guid actionId, Guid edgeId, Guid userId, AZOARequest? request = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, userId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = actionResult.Message };

        var action = actionResult.Result;
        // FR-2 / AC-2c: mutations are blocked while the action is Active.
        if (action.Status == ActionStatus.Active)
            return new AZOAResult<bool> { IsError = true, Message = "Cannot mutate an Active action. Call POST /{id}/unpublish first." };
        var edge = action.Edges.FirstOrDefault(e => e.Id == edgeId);
        if (edge == null)
            return new AZOAResult<bool> { IsError = true, Message = $"Edge {edgeId} not found in action {actionId}." };

        action.Edges.Remove(edge);
        var upsert = await _actionStore.UpsertActionAsync(action);
        if (upsert.IsError)
            return new AZOAResult<bool> { IsError = true, Message = upsert.Message };

        return new AZOAResult<bool> { Result = true, Message = "Edge removed." };
    }

    public async Task<AZOAResult<IEnumerable<Guid>>> GetTopologicalOrderAsync(Guid actionId, Guid userId, AZOARequest? request = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, userId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<IEnumerable<Guid>> { IsError = true, Message = actionResult.Message };

        var action = actionResult.Result;

        // ActionDagValidator is the single ExecutionOrder authority — Validate
        // mutates node.ExecutionOrder in-place. Persist the order so subsequent
        // reads of the action definition see the validator-assigned positions.
        var validation = _dagValidator.Validate(action);
        if (!validation.IsValid)
        {
            return new AZOAResult<IEnumerable<Guid>>
            {
                IsError = true,
                Message = $"DAG validation failed: {string.Join("; ", validation.Errors)}"
            };
        }

        await _actionStore.UpsertActionAsync(action);

        var ordered = action.Nodes
            .OrderBy(n => n.ExecutionOrder)
            .Select(n => n.Id)
            .ToList();

        return new AZOAResult<IEnumerable<Guid>> { Result = ordered, Message = "Success" };
    }

    // ═══════════════════════════════════════════════════════════════════
    // ACTION DEPENDENCIES SUB-RESOURCE
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<ActionDependency>> AddDependencyAsync(Guid actionId, ActionDependencyCreateModel model, Guid userId, AZOARequest? request = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, userId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionDependency> { IsError = true, Message = actionResult.Message };

        var action = actionResult.Result;

        if (model.DependsOnActionId == Guid.Empty)
            return new AZOAResult<ActionDependency> { IsError = true, Message = "DependsOnActionId must not be an empty GUID." };
        if (model.DependsOnActionId == actionId)
            return new AZOAResult<ActionDependency> { IsError = true, Message = "An action may not depend on itself." };

        // A dependency on an action the caller cannot read would turn dependency-status
        // into a probe of someone else's private action. See Managers/AGENTS.md
        // §action-dependency-enforcement.
        var dependsOn = await _actionStore.GetActionAsync(model.DependsOnActionId);
        if (dependsOn.IsError || dependsOn.Result == null
            || !CanReadAuthored(dependsOn.Result.UserId, dependsOn.Result.IsPublic, userId))
            return new AZOAResult<ActionDependency> { IsError = true, Message = "Depended-on action not found." };
        if (model.DependsOnNodeId.HasValue && dependsOn.Result.Nodes.All(n => n.Id != model.DependsOnNodeId.Value))
            return new AZOAResult<ActionDependency> { IsError = true, Message = "Depended-on node not found." };

        var dependency = new ActionDependency
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            DependsOnActionId = model.DependsOnActionId,
            DependsOnNodeId = model.DependsOnNodeId,
            DependencyType = model.DependencyType
        };
        action.Dependencies.Add(dependency);

        var upsert = await _actionStore.UpsertActionAsync(action);
        if (upsert.IsError)
            return new AZOAResult<ActionDependency> { IsError = true, Message = upsert.Message };

        return new AZOAResult<ActionDependency> { Result = dependency, Message = "Dependency added." };
    }

    public async Task<AZOAResult<bool>> RemoveDependencyAsync(Guid actionId, Guid depId, Guid userId, AZOARequest? request = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, userId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = actionResult.Message };

        var action = actionResult.Result;
        var dep = action.Dependencies.FirstOrDefault(d => d.Id == depId);
        if (dep == null)
            return new AZOAResult<bool> { IsError = true, Message = $"Dependency {depId} not found in action {actionId}." };

        action.Dependencies.Remove(dep);
        var upsert = await _actionStore.UpsertActionAsync(action);
        if (upsert.IsError)
            return new AZOAResult<bool> { IsError = true, Message = upsert.Message };

        return new AZOAResult<bool> { Result = true, Message = "Dependency removed." };
    }

    public async Task<AZOAResult<DependencyCheckResult>> CheckDependenciesAsync(Guid actionId, Guid userId, AZOARequest? request = null)
    {
        // Public manual-check endpoint: OWNER-scoped (IDOR — a caller may only probe
        // the dependency state of their OWN action). The run-start enforcement path
        // uses CheckDependencySatisfactionAsync directly on an already-authorized action.
        var actionResult = await LoadOwnedActionAsync(actionId, userId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<DependencyCheckResult> { IsError = true, Message = actionResult.Message };

        var (check, fault) = await CheckDependencySatisfactionAsync(actionResult.Result, userId);
        if (fault != null)
            return new AZOAResult<DependencyCheckResult> { IsError = true, Message = $"Dependency check failed (fail-closed): {fault}" };

        return new AZOAResult<DependencyCheckResult> { Result = check, Message = "Success" };
    }

    /// <summary>
    /// Computes dependency satisfaction for an ALREADY-AUTHORIZED action: a Succeeded run of
    /// each depended-on action by the runner or by the action's owner. Single source of truth
    /// for the manual check and the run-start gate; see Managers/AGENTS.md
    /// §action-dependency-enforcement.
    /// </summary>
    private async Task<(DependencyCheckResult result, string? fault)> CheckDependencySatisfactionAsync(
        ActionDefinition action, Guid runnerUserId)
    {
        var unsatisfied = new List<Guid>();
        foreach (var dep in action.Dependencies)
        {
            var runs = await _runStore.GetByActionIdAsync(dep.DependsOnActionId);
            // Fail closed on a store FAULT: a read error is NOT "unsatisfied" — we
            // cannot prove satisfaction, so surface it distinctly (the run-start gate
            // rejects with a fault message rather than a misleading "not satisfied").
            if (runs.IsError)
                return (default!, $"dependency read for action {dep.DependsOnActionId} failed: {runs.Message}");
            var anySucceeded = runs.Result?.Any(r => r.Status == ActionRunStatus.Succeeded
                && (r.UserId == runnerUserId || r.UserId == action.UserId)) ?? false;
            if (!anySucceeded)
                unsatisfied.Add(dep.Id);
        }

        var result = new DependencyCheckResult
        {
            AllSatisfied = unsatisfied.Count == 0,
            UnsatisfiedDependencyIds = unsatisfied,
            Message = unsatisfied.Count == 0
                ? "All dependencies satisfied."
                : $"{unsatisfied.Count} dependency(ies) not yet satisfied."
        };
        return (result, null);
    }

    /// <summary>
    /// final-hardening F4: ENFORCE action dependencies at run start (fail-closed). Both
    /// run-start paths (<see cref="ExecuteAsync"/> legacy + <see cref="StartWorkflowRunAsync"/>
    /// durable) call this after DAG validation and before any run/exec rows are written —
    /// so an unsatisfied dependency rejects the run cleanly with NO orphaned run created.
    /// Reuses <see cref="CheckDependenciesAsync"/> as the single source of truth for what
    /// "satisfied" means (any Succeeded run of each depended-on action), so the manual
    /// check endpoint and the enforced gate can never diverge. Returns an error result
    /// when a dependency is unsatisfied (or the check itself faults — fail closed);
    /// a null return means the run may proceed. See <c>Managers/AGENTS.md</c>
    /// §action-dependency-enforcement.
    /// </summary>
    private async Task<AZOAResult<ActionRun>?> EnforceDependenciesAsync(ActionDefinition action, Guid runnerUserId, AZOARequest? request)
    {
        // Runs on an ALREADY-AUTHORIZED action (LoadStartableActionAsync ran upstream),
        // so it computes satisfaction directly — NOT via the owner-scoped
        // CheckDependenciesAsync, which would reject a non-owner marketplace run.
        var (depCheck, fault) = await CheckDependencySatisfactionAsync(action, runnerUserId);

        // Fail closed on a store fault (distinct from an unsatisfied dependency).
        if (fault != null)
            return new AZOAResult<ActionRun> { IsError = true, Message = $"Dependency check failed; run rejected (fail-closed): {fault}" };

        if (!depCheck.AllSatisfied)
        {
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"Action {action.Id} cannot start: {depCheck.Message} " +
                          $"Unsatisfied dependency ids: [{string.Join(", ", depCheck.UnsatisfiedDependencyIds)}]. " +
                          "Each depended-on action must have at least one Succeeded run first."
            };
        }

        return null; // all satisfied — proceed
    }

    // ═══════════════════════════════════════════════════════════════════
    // ACTIONRUN READ SURFACE
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<ActionRun>> GetRunAsync(Guid runId, Guid userId, AZOARequest? request = null)
    {
        return await LoadOwnedRunAsync(runId, userId);
    }

    /// <summary>
    /// Pre-run disclosure: the value-moving-node manifest a caller sees BEFORE
    /// committing a marketplace run, so they can consent knowingly. Scoped exactly
    /// like a run-start (owner may preview their own; a non-owner may preview a
    /// published action). ExecutionOrder is (re)assigned so the manifest lists nodes
    /// in run order. See Managers/AGENTS.md §economic-consent.
    /// </summary>
    public async Task<AZOAResult<ActionEconomicManifest>> PreviewRunAsync(Guid actionId, Guid userId, AZOARequest? request = null)
    {
        var (startable, _) = await LoadStartableActionAsync(actionId, userId);
        if (startable.IsError || startable.Result == null)
            return new AZOAResult<ActionEconomicManifest> { IsError = true, Message = startable.Message };

        var action = startable.Result;
        // Assign ExecutionOrder for run-order manifest listing (best-effort — an
        // invalid DAG still yields a manifest, just unordered).
        _dagValidator.Validate(action);

        var manifest = ActionEconomicManifestBuilder.Build(action, _registry, action.PublishedVersionHash);
        return new AZOAResult<ActionEconomicManifest> { Result = manifest, Message = "Run preview computed." };
    }

    public async Task<AZOAResult<IEnumerable<ActionRun>>> ListRunsByActionAsync(Guid actionId, Guid userId, AZOARequest? request = null)
    {
        var owned = await LoadOwnedActionAsync(actionId, userId);
        if (owned.IsError || owned.Result == null)
            return new AZOAResult<IEnumerable<ActionRun>> { IsError = true, Message = owned.Message };

        return await _runStore.GetByActionIdAsync(actionId);
    }

    public async Task<AZOAResult<ActionExecutionState>> GetExecutionStateAsync(Guid runId, Guid userId, AZOARequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, userId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<ActionExecutionState> { IsError = true, Message = runResult.Message };

        var run = runResult.Result;
        var execsResult = await _executionStore.GetByRunIdAsync(runId);
        var execs = (execsResult.Result ?? Enumerable.Empty<ActionNodeExecution>()).ToList();

        // Counts are derived from rows on every read — no risk of drift.
        // "Pending" here groups Pending + Running as the not-yet-terminal bucket
        // because the API consumer cares about "how many are still in flight".
        var state = new ActionExecutionState
        {
            RunId = run.Id,
            ActionId = run.ActionId,
            Status = run.Status,
            StartedAt = run.StartedAt,
            EndedAt = run.EndedAt,
            TotalNodes = execs.Count,
            CompletedNodes = execs.Count(e => e.State == ActionNodeState.Succeeded),
            FailedNodes = execs.Count(e => e.State == ActionNodeState.Failed),
            PendingNodes = execs.Count(e => e.State == ActionNodeState.Pending || e.State == ActionNodeState.Running),
            NodeExecutions = execs
        };

        return new AZOAResult<ActionExecutionState> { Result = state, Message = "Success" };
    }

    public async Task<AZOAResult<ActionRun>> MarkRunCompletedAsync(Guid runId, Guid userId, AZOARequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, userId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = runResult.Message };

        var run = runResult.Result;

        // State-machine guard, mirrored from MarkRunFailedAsync: only Running
        // runs may be transitioned to Succeeded/Failed by this supervisor path.
        if (run.Status != ActionRunStatus.Running)
        {
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"Cannot mark run {runId} completed: status is {run.Status} (only Running runs accept supervisor complete)."
            };
        }

        // In-flight guard: every ActionNodeExecution must be terminal before the
        // run can be marked completed. Pending / Running rows indicate the run
        // still has work outstanding — refusing this transition prevents an
        // orchestrator from prematurely closing a run that has live work.
        var execsResult = await _executionStore.GetByRunIdAsync(runId);
        var execs = (execsResult.Result ?? Enumerable.Empty<ActionNodeExecution>()).ToList();
        var inFlight = execs
            .Where(e => e.State == ActionNodeState.Pending || e.State == ActionNodeState.Running)
            .ToList();
        if (inFlight.Count > 0)
        {
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"Cannot mark run {runId} completed: {inFlight.Count} node execution(s) still in flight (Pending/Running)."
            };
        }

        // Terminal status is Failed if any node execution failed, otherwise
        // Succeeded. Mirrors the same derivation used at the end of
        // ExecuteAsync so the supervisor-driven completion produces the same
        // overall verdict as the in-process loop would have.
        run.Status = execs.Any(e => e.State == ActionNodeState.Failed)
            ? ActionRunStatus.Failed
            : ActionRunStatus.Succeeded;
        run.EndedAt = DateTime.UtcNow;
        var updated = await _runStore.UpdateAsync(run);

        return new AZOAResult<ActionRun> { Result = updated.Result, Message = $"Run marked {run.Status}." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // DURABLE WORKFLOW ENGINE (durable-workflow-engine)
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<AZOAResult<ActionRun>> StartWorkflowRunAsync(Guid actionId, Guid userId, AZOARequest? request = null, Guid? actingTenantId = null, bool acknowledgeEconomicEffects = false)
    {
        var key = request?.IdempotencyKey;
        if (actionId == Guid.Empty || userId == Guid.Empty || string.IsNullOrWhiteSpace(key) || key.Length > 200
            || acknowledgeEconomicEffects && string.IsNullOrWhiteSpace(request?.ExpectedPublishedHash))
            return AZOAResult<ActionRun>.Failure("A request key and, when acknowledging economic effects, the disclosed published hash are required.");
        var runId = ActionWorkflowIdentity.RunId(actionId, userId, actingTenantId, key);
        var invocationIdentity = CanonicalJson.Hash(new { actionId, userId, actingTenantId, key, acknowledgeEconomicEffects,
            request!.ExpectedPublishedHash });
        var existing = await _runStore.GetByIdAsync(runId);
        if (existing.Result is { } saved)
            return saved.UserId == userId && saved.ActingTenantId == actingTenantId && saved.ActionId == actionId
                && saved.InvocationIdentity == invocationIdentity
                ? AZOAResult<ActionRun>.Success(saved) : AZOAResult<ActionRun>.Failure("Workflow request key conflicts with its admitted intent.");
        var (startable, isOwner) = await LoadStartableActionAsync(actionId, userId);
        if (startable.IsError || startable.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = startable.Message };

        var quotaGate = await EnforceRunQuotaAsync(actionId, userId, isOwner);
        if (quotaGate is not null)
            return quotaGate;

        if (startable.Result.Status != ActionStatus.Active)
            return new AZOAResult<ActionRun> { IsError = true, Message = "Action must be published (Active) before a workflow run can be started. Call POST /{id}/publish first." };

        var validationResult = await ValidateDAGAsync(actionId, request);
        if (validationResult.IsError)
            return new AZOAResult<ActionRun> { IsError = true, Message = validationResult.Message };

        var depGate = await EnforceDependenciesAsync(startable.Result, userId, request);
        if (depGate is not null)
            return depGate;

        var actionResult = await _actionStore.GetActionAsync(actionId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = actionResult.Message };
        var action = actionResult.Result;

        if (request.ExpectedPublishedHash is { } expectedHash && action.PublishedVersionHash != expectedHash)
            return AZOAResult<ActionRun>.Failure("The published action differs from the disclosed revision.");

        var consentGate = EnforceEconomicConsent(action, isOwner, acknowledgeEconomicEffects);
        if (consentGate is not null)
            return consentGate;

        var entry = ResolveEntryNode(action);
        if (entry is null)
            return new AZOAResult<ActionRun> { IsError = true, Message = "Action has no entry node to start the workflow run." };

        var confirmed = await _actionStore.TryConfirmActionStateAsync(
            action.Id, ActionStatus.Active, action.Version);
        if (confirmed == 0)
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = "Action run conflict: the action was unpublished or modified concurrently. Reload and retry."
            };

        var run = new ActionRun
        {
            Id = runId,
            InvocationIdentity = invocationIdentity,
            AuthenticationWitnessJson = request?.AuthenticationWitness is { } authentication
                ? CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authentication)) : null,
            ActionId = action.Id,
            UserId = userId,
            ActingTenantId = actingTenantId,
            SourceActionId = isOwner ? null : action.Id,
            OriginUserId = isOwner ? null : action.UserId,
            PublishedVersionHash = action.PublishedVersionHash,
            Status = ActionRunStatus.Pending,
            StartedAt = DateTime.UtcNow
        };
        ActionRunSnapshot.Capture(run, action);
        var admission = _workflowAdmission ?? throw new InvalidOperationException("Atomic workflow admission is not registered.");
        run.Status = ActionRunStatus.Running;
        return await admission.AdmitAsync(new ActionWorkflowAdmission(run, action,
            [new ActionSourceAdmission(action.Id, action.Version, action.PublishedVersionHash, _runQuota.EffectiveLimit(isOwner))], entry.Id));
    }

    /// <summary>
    /// The <c>step(nodeId)</c> primitive: resume a SUSPENDED manual-advance run
    /// from <paramref name="fromNodeId"/> into its single Control successor by
    /// enqueuing that downstream node as a fresh saga step. User-scoped; only
    /// Suspended runs accept advance (mirrors the <see cref="MarkRunCompletedAsync"/>
    /// state-machine guard).
    /// </summary>
    public async Task<AZOAResult<ActionRun>> AdvanceAsync(Guid runId, Guid fromNodeId, Guid userId, AZOARequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, userId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = runResult.Message };
        var run = runResult.Result;

        if (run.Status.IsTerminal())
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"Cannot advance run {runId}: status is {run.Status} (terminal runs cannot advance)."
            };

        if (run.DefinitionSnapshotJson is null)
            return AZOAResult<ActionRun>.Failure("Legacy run has no frozen definition to advance safely.");
        var action = ActionRunSnapshot.Read(run);
        var from = await _executionStore.GetByRunAndNodeAsync(run.Id, fromNodeId);
        if (from.IsError || from.Result?.State != ActionNodeState.Succeeded)
            return AZOAResult<ActionRun>.Failure("Only a successfully completed node can authorize manual advancement.");
        var sourceNode = action.Nodes.SingleOrDefault(node => node.Id == fromNodeId);
        if (sourceNode is null || WorkflowNodeConfig.Parse(sourceNode.Config).Item1 != WorkflowAdvance.Manual)
            return AZOAResult<ActionRun>.Failure("The selected node does not allow manual advancement.");

        if (!from.Result.ManualAdvanceReleased)
        {
            from.Result.ManualAdvanceReleased = true;
            var released = await _executionStore.UpdateAsync(from.Result, ActionNodeState.Succeeded);
            if (released.IsError) return AZOAResult<ActionRun>.Failure(released.Message);
        }
        var admission = _workflowAdmission ?? throw new InvalidOperationException("Atomic workflow admission is not registered.");
        await new ActionDagScheduler(_runStore, _executionStore, admission).AdvanceAsync(action, run.Id);
        return await _runStore.GetByIdAsync(run.Id);
    }

    /// <summary>
    /// Deliver an external signal to a PARKED gate node: un-park the matching
    /// saga step (G2 single-winner via <c>ISagaStore.TrySignalAsync</c>) so the
    /// processor resumes the gate node, carrying <paramref name="payload"/> into
    /// it. User-scoped; idempotent — a duplicate signal un-parks at most once.
    /// </summary>
    public async Task<AZOAResult<ActionRun>> SignalAsync(Guid runId, string gateId, string? payload, Guid userId, AZOARequest? request = null)
    {
        if (string.IsNullOrWhiteSpace(gateId))
            return new AZOAResult<ActionRun> { IsError = true, Message = "Signal requires a non-empty gateId." };

        var runResult = await LoadOwnedRunAsync(runId, userId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = runResult.Message };
        var run = runResult.Result;

        if (run.Status is not (ActionRunStatus.AwaitingSignal or ActionRunStatus.AwaitingTimer or ActionRunStatus.Suspended))
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"Cannot signal run {runId}: status is {run.Status} (only a parked/suspended run accepts a signal)."
            };

        // Build the signal-stamped payload BEFORE the un-park so it is written
        // in the SAME atomic statement (no un-park/stamp race). The new payload
        // is the parked step's payload with SignalPayload set; we re-derive it
        // from the parked row read read-only first. A concurrent signal still
        // races single-winner on the un-park itself.
        var parked = await _sagaStore.GetParkedStepAsync(run.Id.ToString(), gateId, CancellationToken.None);
        string? stampedPayloadJson = null;
        if (parked is not null)
            stampedPayloadJson = BuildSignalStampedPayload(parked.Payload, payload);

        var unparked = await _sagaStore.TrySignalAsync(
            run.Id.ToString(), gateId, stampedPayloadJson, CancellationToken.None);
        if (unparked is null)
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"No node in run {runId} is parked on gate '{gateId}' (already signalled, or wrong gate)."
            };

        run.Status = ActionRunStatus.Running;
        var updated = await _runStore.UpdateAsync(run);
        return new AZOAResult<ActionRun> { Result = updated.Result ?? run, Message = "Signal delivered; workflow run resuming." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // RECONCILE-BEFORE-RETRY RE-PROBE (P7, blockchain-recovery-and-portable-wallets §1.4)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Manual re-probe of a single run parked in
    /// <see cref="ActionRunStatus.AwaitingReconciliation"/>. User-scoped; rejects a
    /// run not in that state. Delegates to <see cref="ReprobeReconciliationAsync"/>,
    /// the shared chain-truth logic that NEVER re-broadcasts.
    /// </summary>
    public async Task<AZOAResult<ActionReconciliationResult>> ReconcileRunAsync(Guid runId, Guid userId, AZOARequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, userId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<ActionReconciliationResult> { IsError = true, Message = runResult.Message };

        var run = runResult.Result;
        if (run.Status != ActionRunStatus.AwaitingReconciliation)
            return new AZOAResult<ActionReconciliationResult>
            {
                IsError = true,
                Message = $"Cannot reconcile run {runId}: status is {run.Status} (only AwaitingReconciliation runs accept a re-probe)."
            };

        var result = await ReprobeReconciliationAsync(run);
        return new AZOAResult<ActionReconciliationResult> { Result = result, Message = "Reconciliation re-probe complete." };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ActionReconciliationSweepResult>> SweepReconciliationAsync(AZOARequest? request = null)
    {
        var report = await SweepParkedRunsAsync(
            TimeSpan.FromSeconds(DefaultParkedRunHardStuckSeconds),
            DefaultParkedRunBatchSize,
            CancellationToken.None);

        if (report.Error is not null)
            return AZOAResult<ActionReconciliationSweepResult>.Failure(report.Error);

        // The scanned count is the honesty signal: zero scanned is "nothing parked",
        // never "swept successfully". See Managers/AGENTS.md §parked-run-sweep.
        var message = report.Scanned == 0
            ? "No runs are awaiting reconciliation."
            : $"Scanned {report.Scanned} run(s) awaiting reconciliation; re-probed {report.Results.Count}, " +
              $"{report.Errors} failed to re-probe, {report.HardStuck.Count} remain parked past the " +
              $"{DefaultParkedRunHardStuckSeconds}s hard-stuck threshold.";

        return new AZOAResult<ActionReconciliationSweepResult>
        {
            Result = new ActionReconciliationSweepResult
            {
                Scanned = report.Scanned,
                Results = report.Results,
                Errors = report.Errors,
                HardStuck = report.HardStuck.Count,
            },
            Message = message
        };
    }

    /// <summary>
    /// Unattended sweep entry point (<see cref="IParkedRunReconciler"/>): re-probe EVERY
    /// run parked in <see cref="ActionRunStatus.AwaitingReconciliation"/>, oldest park
    /// first, and report the runs still parked past <paramref name="hardStuckAfter"/> so
    /// the caller can raise the operator alert. Unscoped (background/operator context);
    /// identical chain-truth logic to <see cref="ReconcileRunAsync"/> - NEVER re-broadcasts.
    /// </summary>
    public Task<ParkedRunSweepReport> ReconcileAllParkedRunsAsync(
        TimeSpan hardStuckAfter, int batchSize, CancellationToken ct = default)
        => SweepParkedRunsAsync(hardStuckAfter, batchSize, ct);

    /// <summary>
    /// The ONE parked-run sweep body behind both seams (the operator endpoint's
    /// <see cref="SweepReconciliationAsync"/> and the background
    /// <see cref="ReconcileAllParkedRunsAsync"/>), so the two can never diverge.
    /// A per-run failure is counted, never fatal: one unreachable RPC must not strand
    /// the rest of the backlog.
    /// </summary>
    private async Task<ParkedRunSweepReport> SweepParkedRunsAsync(
        TimeSpan hardStuckAfter, int batchSize, CancellationToken ct)
    {
        var parked = await _runStore.GetByStatusAsync(ActionRunStatus.AwaitingReconciliation, ct);
        if (parked.IsError)
            return ParkedRunSweepReport.Empty with
            {
                Errors = 1,
                Error = string.IsNullOrWhiteSpace(parked.Message)
                    ? "Failed to list runs awaiting reconciliation."
                    : parked.Message
            };

        var now = DateTime.UtcNow;
        var hardStuckFloor = hardStuckAfter < TimeSpan.Zero ? TimeSpan.Zero : hardStuckAfter;
        var runs = (parked.Result ?? Enumerable.Empty<ActionRun>())
            .OrderBy(r => r.StartedAt)
            .Take(Math.Clamp(batchSize, 1, MaxParkedRunBatchSize))
            .ToList();

        var results = new List<ActionReconciliationResult>(runs.Count);
        var hardStuck = new List<HardStuckParkedRun>();
        var errors = 0;

        foreach (var run in runs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var executions = await LoadRunExecutionsAsync(run.Id);
                var parkedSince = ResolveParkedSinceUtc(run, executions);
                var outcome = await ReprobeReconciliationAsync(run, executions);
                results.Add(outcome);

                // Hard-stuck is judged AFTER the re-probe: a run this pass just un-parked
                // is not stuck, no matter how old its park was.
                if (outcome.Status == ActionRunStatus.AwaitingReconciliation
                    && now - parkedSince >= hardStuckFloor)
                {
                    hardStuck.Add(new HardStuckParkedRun(
                        run.Id, run.ActionId, run.UserId, parkedSince, now - parkedSince,
                        outcome.StillIndeterminate));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                errors++;
            }
        }

        return new ParkedRunSweepReport
        {
            Scanned = runs.Count,
            ReconciledConfirmed = results.Sum(r => r.ReconciledConfirmed),
            ReleasedFailedOnChain = results.Sum(r => r.ReleasedFailedOnChain),
            StillIndeterminate = results.Sum(r => r.StillIndeterminate),
            Errors = errors,
            HardStuck = hardStuck,
            Results = results
        };
    }

    /// <summary>When the run was parked: the newest Failed node end time, else the run start.</summary>
    private static DateTime ResolveParkedSinceUtc(
        ActionRun run, IReadOnlyList<ActionNodeExecution> executions)
    {
        var lastFailedEnd = executions
            .Where(e => e.State == ActionNodeState.Failed && e.EndedAt.HasValue)
            .Select(e => e.EndedAt!.Value)
            .DefaultIfEmpty(default)
            .Max();
        return lastFailedEnd == default ? run.StartedAt : lastFailedEnd;
    }

    /// <summary>Materializes a run's per-node execution rows (empty on a store fault).</summary>
    private async Task<IReadOnlyList<ActionNodeExecution>> LoadRunExecutionsAsync(Guid runId)
    {
        var result = await _executionStore.GetByRunIdAsync(runId);
        return (result.Result ?? Enumerable.Empty<ActionNodeExecution>()).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════
    // INVITATIONS + ACCESS REQUESTS (action-invitations-approval)
    // Run-authorization (RunAccess/InvitedUserIds) is orthogonal to IsPublic.
    // State machine, idempotency, and revoked-mid-run semantics live in
    // Managers/AGENTS.md §action-invitations.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Owner sets the run-access mode + optionally seeds the invite list. Owner-only.</summary>
    public async Task<AZOAResult<ActionDefinition>> SetRunAccessAsync(Guid actionId, Guid ownerUserId, ActionRunAccess runAccess, IEnumerable<Guid>? invitedUserIds = null, AZOARequest? request = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, ownerUserId);
        if (actionResult.IsError || actionResult.Result == null)
            return actionResult;

        var action = actionResult.Result;
        action.RunAccess = runAccess;
        if (invitedUserIds != null)
            // Seed replaces the invite set; owner is always implicitly invited so it is never listed.
            action.InvitedUserIds = invitedUserIds.Where(id => id != ownerUserId).Distinct().ToList();

        return await _actionStore.UpsertActionAsync(action);
    }

    /// <summary>Owner directly adds an invite (idempotent add). Owner-only.</summary>
    public async Task<AZOAResult<ActionDefinition>> InviteUserAsync(Guid actionId, Guid ownerUserId, Guid targetUserId, AZOARequest? request = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, ownerUserId);
        if (actionResult.IsError || actionResult.Result == null)
            return actionResult;

        var action = actionResult.Result;
        if (targetUserId == ownerUserId)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = "Owner is always implicitly invited; no invite needed." };

        if (!action.InvitedUserIds.Contains(targetUserId))
        {
            action.InvitedUserIds.Add(targetUserId);
            return await _actionStore.UpsertActionAsync(action);
        }

        // Idempotent: already invited — no write, return current state.
        return actionResult;
    }

    /// <summary>Owner removes an invite (no-op when absent). In-flight runs are unaffected. Owner-only.</summary>
    public async Task<AZOAResult<ActionDefinition>> RevokeInviteAsync(Guid actionId, Guid ownerUserId, Guid targetUserId, AZOARequest? request = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, ownerUserId);
        if (actionResult.IsError || actionResult.Result == null)
            return actionResult;

        var action = actionResult.Result;
        if (action.InvitedUserIds.Remove(targetUserId))
            return await _actionStore.UpsertActionAsync(action);

        // Idempotent no-op: not invited — nothing to remove.
        return actionResult;
    }

    /// <summary>Any viewer (owner||IsPublic) opens a Pending access request; idempotent per (action, requester). Rejects owner/already-invited.</summary>
    public async Task<AZOAResult<ActionAccessRequest>> RequestAccessAsync(Guid actionId, Guid requesterUserId, string? message = null, AZOARequest? request = null)
    {
        var actionResult = await _actionStore.GetActionAsync(actionId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionAccessRequest> { IsError = true, Message = actionResult.Message ?? "Action not found." };

        var action = actionResult.Result;

        // View gate mirrors GetAsync (owner || IsPublic): a non-viewer cannot even learn the action exists.
        var isOwner = action.UserId == requesterUserId;
        if (!isOwner && !action.IsPublic)
            return new AZOAResult<ActionAccessRequest> { IsError = true, Message = "Action not found." };

        if (isOwner)
            return new AZOAResult<ActionAccessRequest> { IsError = true, Message = "You own this action — no access request needed." };
        if (action.RunAccess != ActionRunAccess.InviteOnly)
            return new AZOAResult<ActionAccessRequest> { IsError = true, Message = "This action is open — anyone may run it, no access request needed." };
        if (action.InvitedUserIds.Contains(requesterUserId))
            return new AZOAResult<ActionAccessRequest> { IsError = true, Message = "You are already invited — no access request needed." };

        // Idempotency: ≤1 Pending per (action, requester). A live Pending is returned as-is;
        // a prior Rejected/Withdrawn does NOT block a fresh Pending. A store FAULT (IsError)
        // fails CLOSED — never fall through to CreateAsync, which would open a duplicate
        // Pending (the store returns a non-error null Result for the genuine not-found case).
        var existing = await _accessRequestStore.GetPendingForActionAndRequesterAsync(actionId, requesterUserId);
        if (existing.IsError)
            return new AZOAResult<ActionAccessRequest> { IsError = true, Message = $"Access-request lookup failed; not opening a new request (fail-closed): {existing.Message}" };
        if (existing.Result != null)
            return existing;

        var accessRequest = new ActionAccessRequest
        {
            Id = Guid.NewGuid(),
            ActionId = actionId,
            RequesterUserId = requesterUserId,
            Status = ActionAccessRequestStatus.Pending,
            Message = message,
            CreatedAt = DateTime.UtcNow
        };
        return await _accessRequestStore.CreateAsync(accessRequest);
    }

    /// <summary>Owner approval queue: all requests for an action, optionally status-filtered. Owner-only.</summary>
    public async Task<AZOAResult<IEnumerable<ActionAccessRequest>>> ListAccessRequestsAsync(Guid actionId, Guid ownerUserId, ActionAccessRequestStatus? status = null, AZOARequest? request = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, ownerUserId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<IEnumerable<ActionAccessRequest>> { IsError = true, Message = actionResult.Message };

        return await _accessRequestStore.GetByActionAsync(actionId, status);
    }

    /// <summary>Owner approves (appends requester to InvitedUserIds) or rejects a Pending request. Scoped by the request's action owner.</summary>
    public async Task<AZOAResult<ActionAccessRequest>> DecideAccessRequestAsync(Guid requestId, Guid ownerUserId, bool approve, string? reason = null, AZOARequest? request = null)
    {
        var reqResult = await _accessRequestStore.GetByIdAsync(requestId);
        if (reqResult.IsError || reqResult.Result == null)
            return new AZOAResult<ActionAccessRequest> { IsError = true, Message = reqResult.Message ?? "Access request not found." };

        var accessRequest = reqResult.Result;

        // IDOR: the caller must own the request's action.
        var actionResult = await LoadOwnedActionAsync(accessRequest.ActionId, ownerUserId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionAccessRequest> { IsError = true, Message = actionResult.Message };

        // Terminal-state immutability (the store enforces no status-CAS — WE enforce it).
        if (accessRequest.Status != ActionAccessRequestStatus.Pending)
            return new AZOAResult<ActionAccessRequest>
            {
                IsError = true,
                Message = $"Cannot decide access request {requestId}: status is {accessRequest.Status} (only Pending requests are decidable)."
            };

        accessRequest.Status = approve ? ActionAccessRequestStatus.Approved : ActionAccessRequestStatus.Rejected;
        accessRequest.DecisionReason = reason;
        accessRequest.DecidedAt = DateTime.UtcNow;
        accessRequest.DecidedByUserId = ownerUserId;

        if (approve)
        {
            // Approve mints an invitation: append the requester to InvitedUserIds.
            var action = actionResult.Result;
            if (!action.InvitedUserIds.Contains(accessRequest.RequesterUserId))
            {
                action.InvitedUserIds.Add(accessRequest.RequesterUserId);
                var upsert = await _actionStore.UpsertActionAsync(action);
                if (upsert.IsError)
                    return new AZOAResult<ActionAccessRequest> { IsError = true, Message = $"Failed to persist invite: {upsert.Message}" };
            }
        }

        return await _accessRequestStore.UpdateAsync(accessRequest);
    }

    /// <summary>Requester withdraws their own Pending request. Scoped by requester identity.</summary>
    public async Task<AZOAResult<ActionAccessRequest>> WithdrawAccessRequestAsync(Guid requestId, Guid requesterUserId, AZOARequest? request = null)
    {
        var reqResult = await _accessRequestStore.GetByIdAsync(requestId);
        if (reqResult.IsError || reqResult.Result == null)
            return new AZOAResult<ActionAccessRequest> { IsError = true, Message = reqResult.Message ?? "Access request not found." };

        var accessRequest = reqResult.Result;

        // IDOR: only the requester may withdraw their own request.
        if (accessRequest.RequesterUserId != requesterUserId)
            return new AZOAResult<ActionAccessRequest> { IsError = true, Message = "Access request belongs to a different user." };

        if (accessRequest.Status != ActionAccessRequestStatus.Pending)
            return new AZOAResult<ActionAccessRequest>
            {
                IsError = true,
                Message = $"Cannot withdraw access request {requestId}: status is {accessRequest.Status} (only Pending requests are withdrawable)."
            };

        accessRequest.Status = ActionAccessRequestStatus.Withdrawn;
        accessRequest.DecidedAt = DateTime.UtcNow;
        accessRequest.DecidedByUserId = requesterUserId;

        return await _accessRequestStore.UpdateAsync(accessRequest);
    }

    /// <summary>Requester's own outbound requests across any action, optionally status-filtered.</summary>
    public async Task<AZOAResult<IEnumerable<ActionAccessRequest>>> ListMyAccessRequestsAsync(Guid requesterUserId, ActionAccessRequestStatus? status = null, AZOARequest? request = null)
    {
        return await _accessRequestStore.GetByRequesterAsync(requesterUserId, status);
    }

    private async Task<ActionReconciliationResult> ReprobeReconciliationAsync(
        ActionRun run, IReadOnlyList<ActionNodeExecution>? preloadedExecutions = null)
    {
        var outcome = new ActionReconciliationResult { RunId = run.Id, Status = run.Status };
        var rows = preloadedExecutions ?? await LoadRunExecutionsAsync(run.Id);
        foreach (var execution in rows.Where(row => row.State is ActionNodeState.Running or ActionNodeState.Failed))
        {
            if (execution.State != ActionNodeState.Running || execution.ExternalOperation is null)
            {
                outcome.StillIndeterminate++;
                continue;
            }
            var observed = await _operationRecovery.ObserveAsync(execution, run, CancellationToken.None);
            if (observed.Status is ActionExternalOperationStatus.Pending or ActionExternalOperationStatus.Ambiguous)
            {
                outcome.StillIndeterminate++;
                continue;
            }
            execution.ExternalOperation = observed.Reference;
            execution.Output = observed.Output;
            execution.Error = observed.Detail;
            execution.TxHash = observed.TransactionId;
            execution.ChainType = observed.ChainType;
            execution.State = observed.Status == ActionExternalOperationStatus.Succeeded ? ActionNodeState.Succeeded
                : observed.Status == ActionExternalOperationStatus.Cancelled ? ActionNodeState.Cancelled : ActionNodeState.Failed;
            execution.EndedAt = DateTime.UtcNow;
            execution.RecoveryState = observed.Status.ToString();
            var saved = await _executionStore.UpdateAsync(execution, ActionNodeState.Running);
            if (saved.IsError) { outcome.StillIndeterminate++; continue; }
            await _sagaStore.TrySignalAsync(run.Id.ToString(), $"operator:{execution.NodeId:N}", null, CancellationToken.None);
            if (execution.State == ActionNodeState.Succeeded) outcome.ReconciledConfirmed++;
            else outcome.ReleasedFailedOnChain++;
        }
        var refreshed = await _runStore.GetByIdAsync(run.Id);
        outcome.Status = refreshed.Result?.Status ?? run.Status;
        return outcome;
    }

    /// <summary>The entry node: the explicit <c>IsEntry</c> node, else the
    /// lowest <c>ExecutionOrder</c> node (the validator's topological head).</summary>
    private static ActionNode? ResolveEntryNode(ActionDefinition action)
    {
        var entry = action.Nodes.FirstOrDefault(n => n.IsEntry);
        if (entry is not null)
            return entry;
        return action.Nodes.OrderBy(n => n.ExecutionOrder).FirstOrDefault();
    }

    /// <summary>Re-serialize the parked gate step's payload with the delivered
    /// signal body so the resumed node sees <c>SignalPayload != null</c> and
    /// falls through to do its work instead of re-parking. Pure: the result is
    /// handed to <c>TrySignalAsync</c> to write atomically with the un-park.
    /// Returns <c>null</c> when the existing payload is unparseable (leave it
    /// as-is; the node re-parks — safe, not lost).</summary>
    private static string? BuildSignalStampedPayload(string existingPayloadJson, string? signalPayload)
    {
        ActionStepPayload? parsed;
        try
        {
            parsed = System.Text.Json.JsonSerializer.Deserialize<ActionStepPayload>(existingPayloadJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (parsed is null)
            return null;

        var stamped = parsed with { SignalPayload = signalPayload ?? string.Empty };
        return SagaStep<ActionStepPayload>.Serialize(stamped);
    }
}

/// <summary>
/// The unattended parked-run reconciliation seam: re-probe every run parked in
/// <see cref="ActionRunStatus.AwaitingReconciliation"/> without a human naming it.
/// Implemented by <see cref="ActionManager"/> and consumed by the background
/// reconciliation tick (<c>Services/Reconciliation/ReconciliationHostedService</c>).
/// </summary>
/// <remarks>
/// Declared here rather than under <c>Interfaces/Managers/</c> because the wave that
/// introduced it owns only this file; promote it to its own file when that constraint
/// lifts.
/// </remarks>
public interface IParkedRunReconciler
{
    /// <summary>Sweep every parked run, oldest park first, capped at
    /// <paramref name="batchSize"/>; runs still parked after their re-probe and older
    /// than <paramref name="hardStuckAfter"/> come back in
    /// <see cref="ParkedRunSweepReport.HardStuck"/> for the caller to alert on.</summary>
    Task<ParkedRunSweepReport> ReconcileAllParkedRunsAsync(
        TimeSpan hardStuckAfter, int batchSize, CancellationToken ct = default);
}

/// <summary>One run still parked past the hard-stuck threshold - the operator-alert payload.</summary>
public sealed record HardStuckParkedRun(
    Guid RunId,
    Guid ActionId,
    Guid UserId,
    DateTime ParkedSinceUtc,
    TimeSpan Age,
    int IndeterminateNodes);

/// <summary>Outcome of one parked-run sweep pass. Immutable.</summary>
public sealed record ParkedRunSweepReport
{
    /// <summary>Runs examined this pass (after the batch cap).</summary>
    public int Scanned { get; init; }

    /// <summary>Nodes whose tx was Confirmed and reconciled to Succeeded - no re-broadcast.</summary>
    public int ReconciledConfirmed { get; init; }

    /// <summary>Nodes provably FailedOnChain and released to retry/compensation.</summary>
    public int ReleasedFailedOnChain { get; init; }

    /// <summary>Nodes whose chain truth is still not derivable - left parked.</summary>
    public int StillIndeterminate { get; init; }

    /// <summary>Per-run probe failures, plus 1 when the parked-run listing itself faulted.</summary>
    public int Errors { get; init; }

    /// <summary>Set only when the parked-run listing faulted, so nothing was swept.</summary>
    public string? Error { get; init; }

    /// <summary>Runs still parked after this pass AND older than the hard-stuck threshold.</summary>
    public IReadOnlyList<HardStuckParkedRun> HardStuck { get; init; } = Array.Empty<HardStuckParkedRun>();

    /// <summary>One re-probe outcome per run swept, in sweep order.</summary>
    public IReadOnlyList<ActionReconciliationResult> Results { get; init; } = Array.Empty<ActionReconciliationResult>();

    public static ParkedRunSweepReport Empty { get; } = new();

    public override string ToString() =>
        $"scanned={Scanned} reconciled={ReconciledConfirmed} released={ReleasedFailedOnChain} " +
        $"indeterminate={StillIndeterminate} hardStuck={HardStuck.Count} errors={Errors}";
}
