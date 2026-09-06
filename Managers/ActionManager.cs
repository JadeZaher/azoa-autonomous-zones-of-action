using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Sagas;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Predicates;
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
public class ActionManager : IActionManager
{
    private readonly IActionStore _actionStore;
    private readonly IActionRunStore _runStore;
    private readonly IActionNodeExecutionStore _executionStore;
    private readonly IActionAccessRequestStore _accessRequestStore;
    private readonly IActionDagValidator _dagValidator;
    private readonly IActionDagExecutabilityValidator _executabilityValidator;
    private readonly IActionNodeHandlerRegistry _registry;
    private readonly ISagaStore _sagaStore;
    private readonly IBlockchainProviderFactory _chainFactory;
    private readonly ActionConfigBindingResolver _bindingResolver;

    // D1/D9 pre-execution value-rail capability gate. Constructed here (not injected) so
    // the DI signature stays positional-compatible; the pluggable half — the per-rail
    // destination directories — IS injected. See Services/Actions/AGENTS.md §rail-capability.
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
        IEnumerable<IRailDestinationDirectory>? railDestinationDirectories = null)
    {
        _actionStore = actionStore;
        _runStore = runStore;
        _executionStore = executionStore;
        _accessRequestStore = accessRequestStore!;
        _dagValidator = dagValidator;
        _executabilityValidator = executabilityValidator;
        _registry = registry;
        _sagaStore = sagaStore;
        _chainFactory = chainFactory;
        _bindingResolver = bindingResolver;
        _runQuota = configuration is null
            ? new ActionRunQuotaOptions()
            : ActionRunQuotaOptions.FromConfiguration(configuration);
        _railGate = new ValueRailCapabilityGate(
            walletManager, chainFactory.DefaultRailId, railDestinationDirectories);
    }

    /// <summary>
    /// Loads an action and rejects when it is owned by a different user. Returns
    /// the loaded action on success, or an error result to surface verbatim.
    /// </summary>
    private async Task<AZOAResult<ActionDefinition>> LoadOwnedActionAsync(Guid actionId, Guid userId)
    {
        var actionResult = await _actionStore.GetActionAsync(actionId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = actionResult.Message ?? "Action not found." };
        if (actionResult.Result.UserId != userId)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = "Action is owned by a different user." };
        return actionResult;
    }

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
            return (new AZOAResult<ActionDefinition> { IsError = true, Message = actionResult.Message ?? "Action not found." }, false);

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
            return new AZOAResult<ActionRun> { IsError = true, Message = runResult.Message ?? "Action run not found." };
        if (runResult.Result.UserId != userId)
            return new AZOAResult<ActionRun> { IsError = true, Message = "Action run is owned by a different user." };
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
            return new AZOAResult<ActionDefinition> { IsError = true, Message = actionResult.Message ?? "Action not found." };
        var action = actionResult.Result;
        if (action.UserId == userId || action.IsPublic)
            return actionResult;
        return new AZOAResult<ActionDefinition> { IsError = true, Message = "Action is owned by a different user." };
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

        // Full validation stack: structural DAG + fan-out-as-error + config check.
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

    public async Task<AZOAResult<ActionRun>> ExecuteAsync(Guid actionId, Guid userId, AZOARequest? request = null, Guid? actingTenantId = null, bool acknowledgeEconomicEffects = false)
    {
        // Marketplace mechanic: the owner may run their own action; a non-owner may
        // run it only when it is published (public + Active). Provenance back to the
        // origin action/creator is stamped on the run below for the non-owner path.
        var (startable, isOwner) = await LoadStartableActionAsync(actionId, userId);
        if (startable.IsError || startable.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = startable.Message };

        // Marketplace guard 1 — per-(user,action) run-start quota (treasury/runner drain).
        var quotaGate = await EnforceRunQuotaAsync(actionId, userId, isOwner);
        if (quotaGate is not null)
            return quotaGate;

        // FR-2 (action-dag-semantic-hardening): require Active status before execution.
        // (The non-owner path already enforced Active in LoadStartableActionAsync; this
        // keeps the owner path's original gate + error message intact.)
        if (startable.Result.Status != ActionStatus.Active)
            return new AZOAResult<ActionRun> { IsError = true, Message = "Action must be published (Active) before it can be executed. Call POST /{id}/publish first." };

        // Validate DAG first (also assigns ExecutionOrder onto definition nodes).
        var validationResult = await ValidateDAGAsync(actionId, request);
        if (validationResult.IsError)
            return new AZOAResult<ActionRun> { IsError = true, Message = validationResult.Message };

        // final-hardening F4: enforce action dependencies at run start (fail-closed).
        // Rejected BEFORE any run/exec rows are written — no orphaned run on rejection.
        // Uses the already-authorized action (startable.Result) so a non-owner
        // marketplace run isn't rejected by owner-scoped dependency reads.
        var depGate = await EnforceDependenciesAsync(startable.Result, request);
        if (depGate is not null)
            return depGate;

        var actionResult = await _actionStore.GetActionAsync(actionId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = actionResult.Message };

        var action = actionResult.Result;

        // Marketplace guard 2 — economic-consent gate. DAG validation above assigned
        // ExecutionOrder, so the manifest lists value-moving nodes in run order. A
        // non-owner run containing economic nodes is rejected unless acknowledged.
        var consentGate = EnforceEconomicConsent(action, isOwner, acknowledgeEconomicEffects);
        if (consentGate is not null)
            return consentGate;

        // F6 TOCTOU guard: confirm the definition is STILL Active at the version we
        // just read before we commit a run against it. This closes unpublish racing
        // a run-start — if an UnpublishAsync flipped Active → Draft (bumping version)
        // between our ownership read and here, the confirm misses and we reject
        // rather than executing a torn/unpublished definition.
        var confirmed = await _actionStore.TryConfirmActionStateAsync(
            action.Id, ActionStatus.Active, action.Version);
        if (confirmed == 0)
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = "Action run conflict: the action was unpublished or modified concurrently. Reload and retry."
            };

        // Create the ActionRun upfront — Pending → Running on first node claim.
        // The run is ALWAYS stamped to the RUNNER (userId), not the action owner —
        // on the owner path they are equal; on the non-owner (marketplace) path the
        // runner gets a run under their own context with provenance back to origin.
        var run = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            UserId = userId,
            // tenant-consent-delegation AC4: stamp the acting tenant from the
            // activating principal so Tier-2 nodes carry it to the signing seam.
            ActingTenantId = actingTenantId,
            // Marketplace provenance: null on the owner path, origin action + creator
            // on the non-owner path.
            SourceActionId = isOwner ? null : action.Id,
            OriginUserId = isOwner ? null : action.UserId,
            // Bind the run to the exact published graph revision (bait-and-switch guard).
            PublishedVersionHash = action.PublishedVersionHash,
            Status = ActionRunStatus.Pending,
            StartedAt = DateTime.UtcNow
        };
        var createRun = await _runStore.CreateAsync(run);
        if (createRun.IsError || createRun.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = createRun.Message };

        // Pre-create one ActionNodeExecution(Pending) per action node.
        foreach (var node in action.Nodes)
        {
            var exec = new ActionNodeExecution
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                NodeId = node.Id,
                State = ActionNodeState.Pending,
                StartedAt = DateTime.UtcNow
            };
            var createExec = await _executionStore.CreateAsync(exec);
            if (createExec.IsError)
                return new AZOAResult<ActionRun> { IsError = true, Message = createExec.Message };
        }

        // Status: Pending → Running (first claim).
        run.Status = ActionRunStatus.Running;
        await _runStore.UpdateAsync(run);

        // Track per-node executions in-memory for ComposeOutputs upstream lookup.
        var executionsByNode = new Dictionary<Guid, ActionNodeExecution>();

        // Execute nodes in topological order.
        var sortedNodes = action.Nodes.OrderBy(n => n.ExecutionOrder).ToList();

        foreach (var node in sortedNodes)
        {
            // V2 skip rule (Managers/AGENTS.md §onfailure-semantics):
            // 1. Any Control/Conditional source Failed or Skipped → skip.
            // 2. OnFailure edges are excluded from rule 1; they activate when
            //    source Failed and skip when source Succeeded/Skipped (inverse).
            // 3. When ≥1 OnFailure edges exist: node runs only if ≥1 OnFailure
            //    source Failed (and no Control/Conditional source failed rule 1).
            var incomingEdges = action.Edges.Where(e => e.TargetNodeId == node.Id).ToList();
            var shouldSkip = false;

            var onFailureEdges = incomingEdges.Where(e => e.EdgeType == ActionEdgeType.OnFailure).ToList();
            var controlEdges = incomingEdges.Where(
                e => e.EdgeType == ActionEdgeType.Control || e.EdgeType == ActionEdgeType.Conditional).ToList();

            // Rule 1: Control/Conditional source Failed or Skipped → skip.
            foreach (var edge in controlEdges)
            {
                executionsByNode.TryGetValue(edge.SourceNodeId, out var sourceExec);
                var sourceState = sourceExec?.State;

                if (edge.EdgeType == ActionEdgeType.Conditional)
                {
                    if (sourceState == ActionNodeState.Failed || sourceState == ActionNodeState.Skipped)
                    {
                        shouldSkip = true;
                        break;
                    }
                }

                if (edge.EdgeType == ActionEdgeType.Control &&
                    (sourceState == ActionNodeState.Failed || sourceState == ActionNodeState.Skipped))
                {
                    shouldSkip = true;
                    break;
                }
            }

            // Rule 2+3: OnFailure edges (inverse activation).
            if (!shouldSkip && onFailureEdges.Count > 0)
            {
                var anyOnFailureSourceFailed = onFailureEdges.Any(e =>
                {
                    executionsByNode.TryGetValue(e.SourceNodeId, out var ex);
                    return ex?.State == ActionNodeState.Failed;
                });
                // Skip unless at least one OnFailure source actually Failed.
                if (!anyOnFailureSourceFailed)
                    shouldSkip = true;
            }

            var execution = (await _executionStore.GetByRunAndNodeAsync(run.Id, node.Id)).Result!;

            if (shouldSkip)
            {
                execution.State = ActionNodeState.Skipped;
                execution.EndedAt = DateTime.UtcNow;
                // HIGH#7 G2 guard: only mark Skipped if the row is still
                // Pending. If a concurrent ForkAsync flipped it to Cancelled
                // we must not silently overwrite — the store returns an
                // error result and we simply re-read the latest state for
                // downstream ComposeOutputs lookup.
                var skipUpdate = await _executionStore.UpdateAsync(
                    execution, expectedState: ActionNodeState.Pending);
                if (skipUpdate.IsError)
                {
                    execution = (await _executionStore.GetByRunAndNodeAsync(run.Id, node.Id)).Result ?? execution;
                }
                executionsByNode[node.Id] = execution;
                continue;
            }

            // Claim the row (Pending → Running). G2 conditional-update guard.
            var claim = await _executionStore.TryClaimPendingAsync(run.Id, node.Id);
            if (claim.IsError || claim.Result == null)
            {
                // Either missing or already-claimed (lost race). Treat as fail.
                execution.State = ActionNodeState.Failed;
                execution.Error = claim.Message ?? "Failed to claim node for execution.";
                execution.EndedAt = DateTime.UtcNow;
                // Best-effort unconditional update — the claim already
                // observed a drift, so a second guard would be redundant.
                await _executionStore.UpdateAsync(execution);
                executionsByNode[node.Id] = execution;
                break;
            }
            execution = claim.Result!;

            // Build upstream-output map for ComposeOutputs (predecessors only).
            var upstream = new Dictionary<Guid, ActionNodeExecution>();
            foreach (var edge in incomingEdges)
            {
                if (executionsByNode.TryGetValue(edge.SourceNodeId, out var pe))
                    upstream[edge.SourceNodeId] = pe;
            }

            // FR-1 ($from binding) — resolve before handler dispatch.
            // See Services/Actions/AGENTS.md §output-binding.
            // C1/H1: identity side-effects and holon-scoped $from binding resolve
            // against the RUNNER (run.UserId), never the action owner. On the owner
            // path run.UserId == action.UserId, so behaviour is unchanged.
            var bindingResult = await _bindingResolver.TryResolveAsync(
                node.Config, node, action, upstream, executionsByNode, run.UserId, CancellationToken.None);

            ActionNodeHandlerResult result;
            if (!bindingResult.Ok)
            {
                result = ActionNodeResults.Fail($"$from binding error on node '{node.Name}': {bindingResult.Error}");
            }
            else if (!_registry.TryGet(node.NodeType, out var handler))
            {
                result = ActionNodeResults.Fail($"Unsupported node type: {node.NodeType}");
            }
            else if (handler.RequiresChainCapability
                && !await _railGate.HasUsableDestinationAsync(
                    run.UserId, handler.ResolveSettlementRailId(node)))
            {
                // D1/D9 pre-execution capability gate — fails closed (no broadcast): a
                // value-moving node may not run unless the actor has a usable destination
                // on the rail THIS node settles on. HandleAsync is SKIPPED entirely.
                result = ActionNodeResults.Fail(ValueRailCapabilityGate.NoDestinationMessage(
                    _railGate.ResolveRailId(handler.ResolveSettlementRailId(node))));
            }
            else
            {
                try
                {
                    // Temporarily swap in the binding-resolved config so context.Node.Config
                    // carries the resolved values. Restored after the call (ExecuteAsync is
                    // sequential; the definition node object is not shared across calls).
                    var originalConfig = node.Config;
                    // Ok==true (checked above) guarantees ResolvedJson is non-null.
                    node.Config = bindingResult.ResolvedJson!;
                    try
                    {
                        var ctx = new ActionNodeExecutionContext(run.Id, node.Id, action, run.UserId, upstream, run.ActingTenantId, executionsByNode);
                        result = await handler.HandleAsync(ctx);
                    }
                    finally
                    {
                        node.Config = originalConfig;
                    }
                }
                catch (Exception ex)
                {
                    result = ActionNodeResults.Fail(ex.Message);
                }
            }

            if (result.IsError)
            {
                execution.State = ActionNodeState.Failed;
                execution.Error = result.Message;
            }
            else
            {
                execution.State = ActionNodeState.Succeeded;
                execution.Output = result.Output;
            }
            execution.EndedAt = DateTime.UtcNow;
            // HIGH#7 — only commit the terminal state when the row is still
            // Running. A concurrent fork (or supervisor-fail) may have
            // flipped the row to Cancelled between our claim and our
            // completion; in that case the guard rejects the overwrite and
            // we accept the fork's decision rather than resurrecting the
            // execution into Succeeded/Failed.
            var terminalUpdate = await _executionStore.UpdateAsync(
                execution, expectedState: ActionNodeState.Running);
            if (terminalUpdate.IsError)
            {
                execution = (await _executionStore.GetByRunAndNodeAsync(run.Id, node.Id)).Result ?? execution;
            }
            executionsByNode[node.Id] = execution;
        }

        // V3 run-status: Failed iff any UNHANDLED Failed execution exists.
        // A Failed node is "handled" when it has ≥1 outgoing OnFailure edge
        // (the failure arm activated, so the run can still succeed).
        // See Managers/AGENTS.md §onfailure-semantics.
        var allExecs = (await _executionStore.GetByRunIdAsync(run.Id)).Result ?? Enumerable.Empty<ActionNodeExecution>();
        var nodesWithOnFailureOut = action.Edges
            .Where(e => e.EdgeType == ActionEdgeType.OnFailure)
            .Select(e => e.SourceNodeId)
            .ToHashSet();
        var hasUnhandledFailure = allExecs.Any(e =>
            e.State == ActionNodeState.Failed && !nodesWithOnFailureOut.Contains(e.NodeId));
        run.Status = hasUnhandledFailure ? ActionRunStatus.Failed : ActionRunStatus.Succeeded;
        run.EndedAt = DateTime.UtcNow;
        var updated = await _runStore.UpdateAsync(run);

        return new AZOAResult<ActionRun> { Result = updated.Result, Message = $"Action run {run.Status}." };
    }

    public async Task<AZOAResult<ActionNodeExecution>> ExecuteNodeAsync(Guid actionId, Guid nodeId, Guid userId, AZOARequest? request = null, Guid? actingTenantId = null)
    {
        var actionResult = await LoadOwnedActionAsync(actionId, userId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionNodeExecution> { IsError = true, Message = actionResult.Message };

        var action = actionResult.Result;
        var node = action.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return new AZOAResult<ActionNodeExecution> { IsError = true, Message = "Node not found." };

        // Single-node execution creates an ad-hoc ActionRun that lives just long
        // enough to record this one node's outcome. This preserves the
        // historic ExecuteNodeAsync entry point (used by the ActionController)
        // while keeping the new (runId, nodeId) invariant: no state ever lands
        // on the ActionNode itself.
        var run = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            UserId = action.UserId,
            // tenant-consent-delegation AC4: carry the acting tenant onto the
            // ad-hoc one-node run so the dispatched handler reaches the seam.
            ActingTenantId = actingTenantId,
            Status = ActionRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await _runStore.CreateAsync(run);

        var execution = new ActionNodeExecution
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            NodeId = node.Id,
            State = ActionNodeState.Running,
            StartedAt = DateTime.UtcNow
        };
        await _executionStore.CreateAsync(execution);

        // FR-1 ($from binding) — resolve before handler dispatch (single-node path).
        // Single-node path is owner-only (LoadOwnedActionAsync), so run.UserId ==
        // action.UserId here; use run.UserId for parity with the multi-node path.
        var bindingResultSingle = await _bindingResolver.TryResolveAsync(
            node.Config, node, action, new Dictionary<Guid, ActionNodeExecution>(),
            new Dictionary<Guid, ActionNodeExecution>(), run.UserId, CancellationToken.None);

        ActionNodeHandlerResult result;
        if (!bindingResultSingle.Ok)
        {
            result = ActionNodeResults.Fail($"$from binding error on node '{node.Name}': {bindingResultSingle.Error}");
        }
        else if (!_registry.TryGet(node.NodeType, out var handler))
        {
            result = ActionNodeResults.Fail($"Unsupported node type: {node.NodeType}");
        }
        else
        {
            try
            {
                var originalConfigSingle = node.Config;
                // Ok==true (checked above) guarantees ResolvedJson is non-null.
                node.Config = bindingResultSingle.ResolvedJson!;
                try
                {
                    var ctx = new ActionNodeExecutionContext(run.Id, node.Id, action, run.UserId, upstreamExecutions: null, actingTenantId: run.ActingTenantId);
                    result = await handler.HandleAsync(ctx);
                }
                finally
                {
                    node.Config = originalConfigSingle;
                }
            }
            catch (Exception ex)
            {
                result = ActionNodeResults.Fail(ex.Message);
            }
        }

        if (result.IsError)
        {
            execution.State = ActionNodeState.Failed;
            execution.Error = result.Message;
        }
        else
        {
            execution.State = ActionNodeState.Succeeded;
            execution.Output = result.Output;
        }
        execution.EndedAt = DateTime.UtcNow;
        // HIGH#7 — same G2 guard as the in-loop terminal update at line ~308.
        // The ad-hoc single-node run created the execution in Running state,
        // so we only commit Succeeded/Failed when the row is still Running.
        await _executionStore.UpdateAsync(execution, expectedState: ActionNodeState.Running);

        run.Status = result.IsError ? ActionRunStatus.Failed : ActionRunStatus.Succeeded;
        run.EndedAt = DateTime.UtcNow;
        await _runStore.UpdateAsync(run);

        return result.IsError
            ? new AZOAResult<ActionNodeExecution> { IsError = true, Result = execution, Message = result.Message ?? string.Empty }
            : new AZOAResult<ActionNodeExecution> { Result = execution, Message = "Node executed successfully." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // FORK
    // ═══════════════════════════════════════════════════════════════════

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

        var actionResult = await _actionStore.GetActionAsync(parent.ActionId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = actionResult.Message };

        var action = actionResult.Result;
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
            ForkReason = reason
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

    public async Task<AZOAResult<ActionTemplate>> GetTemplateAsync(Guid id, AZOARequest? request = null)
    {
        return await _actionStore.GetActionTemplateAsync(id);
    }

    public async Task<AZOAResult<IEnumerable<ActionTemplate>>> ListTemplatesAsync(AZOARequest? request = null)
    {
        return await _actionStore.GetAllActionTemplatesAsync();
    }

    public async Task<AZOAResult<ActionDefinition>> InstantiateTemplateAsync(Guid templateId, Guid userId, Dictionary<string, string>? parameters = null, AZOARequest? request = null)
    {
        var templateResult = await _actionStore.GetActionTemplateAsync(templateId);
        if (templateResult.IsError || templateResult.Result == null)
            return new AZOAResult<ActionDefinition> { IsError = true, Message = templateResult.Message };

        var template = templateResult.Result;

        // Load node templates referenced by this action template
        var nodeTemplatesResult = await _actionStore.GetAllActionNodeTemplatesAsync();
        var nodeTemplates = (nodeTemplatesResult.Result ?? Enumerable.Empty<ActionNodeTemplate>())
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

    public async Task<AZOAResult<IEnumerable<ActionNodeTemplate>>> ListNodeTemplatesAsync(AZOARequest? request = null)
    {
        return await _actionStore.GetAllActionNodeTemplatesAsync();
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

        var (check, fault) = await CheckDependencySatisfactionAsync(actionResult.Result);
        if (fault != null)
            return new AZOAResult<DependencyCheckResult> { IsError = true, Message = $"Dependency check failed (fail-closed): {fault}" };

        return new AZOAResult<DependencyCheckResult> { Result = check, Message = "Success" };
    }

    /// <summary>
    /// Computes dependency satisfaction for an ALREADY-AUTHORIZED action (any Succeeded
    /// run of each depended-on action). No ownership scoping — the caller must have
    /// established run-authorization first (LoadStartableActionAsync / LoadOwnedActionAsync).
    /// Single source of truth shared by the manual check endpoint and the run-start gate.
    /// </summary>
    private async Task<(DependencyCheckResult result, string? fault)> CheckDependencySatisfactionAsync(ActionDefinition action)
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
            var anySucceeded = runs.Result?.Any(r => r.Status == ActionRunStatus.Succeeded) ?? false;
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
    private async Task<AZOAResult<ActionRun>?> EnforceDependenciesAsync(ActionDefinition action, AZOARequest? request)
    {
        // Runs on an ALREADY-AUTHORIZED action (LoadStartableActionAsync ran upstream),
        // so it computes satisfaction directly — NOT via the owner-scoped
        // CheckDependenciesAsync, which would reject a non-owner marketplace run.
        var (depCheck, fault) = await CheckDependencySatisfactionAsync(action);

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

    /// <summary>
    /// Start a durable, suspendable workflow run. Mirrors <see cref="ExecuteAsync"/>'s
    /// run + per-node-execution creation (so the existing read surface and
    /// fork/supervisor paths keep working), then — instead of the in-process
    /// node <c>foreach</c> — enqueues the ENTRY node as the first saga step. The
    /// saga processor advances the DAG asynchronously, suspending at
    /// manual/gate/timer nodes (durable-workflow-engine D1, Phase 2).
    /// </summary>
    public async Task<AZOAResult<ActionRun>> StartWorkflowRunAsync(Guid actionId, Guid userId, AZOARequest? request = null, Guid? actingTenantId = null, bool acknowledgeEconomicEffects = false)
    {
        // Marketplace mechanic (durable path): owner may start their own action; a
        // non-owner may start it only when published (public + Active). Provenance is
        // stamped on the run below, mirroring the legacy ExecuteAsync path.
        var (startable, isOwner) = await LoadStartableActionAsync(actionId, userId);
        if (startable.IsError || startable.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = startable.Message };

        // Marketplace guard 1 — per-(user,action) run-start quota (treasury/runner drain).
        var quotaGate = await EnforceRunQuotaAsync(actionId, userId, isOwner);
        if (quotaGate is not null)
            return quotaGate;

        // FR-2: require Active status before starting a durable workflow run.
        if (startable.Result.Status != ActionStatus.Active)
            return new AZOAResult<ActionRun> { IsError = true, Message = "Action must be published (Active) before a workflow run can be started. Call POST /{id}/publish first." };

        var validationResult = await ValidateDAGAsync(actionId, request);
        if (validationResult.IsError)
            return new AZOAResult<ActionRun> { IsError = true, Message = validationResult.Message };

        // FR-3 / AC-3a: fan-out is an error on the durable path — reject before any
        // saga rows are written (ResolveSingleSuccessor catches it at runtime too,
        // but catching here gives a cleaner, pre-run error message).
        var actionForFanOut = (await _actionStore.GetActionAsync(actionId)).Result;
        if (actionForFanOut != null)
        {
            var fanOutCheck = _dagValidator.Validate(actionForFanOut, fanOutAsError: true);
            var fanOutErrors = fanOutCheck.Errors.Where(e => e.Contains("fan-out", StringComparison.OrdinalIgnoreCase)).ToList();
            if (fanOutErrors.Count > 0)
                return new AZOAResult<ActionRun> { IsError = true, Message = $"Workflow run rejected — fan-out not supported on durable path: {string.Join("; ", fanOutErrors)}" };
        }

        // final-hardening F4: enforce action dependencies at run start (fail-closed).
        // Rejected BEFORE the run + per-node executions are written and before the entry
        // node is enqueued as a saga step — no orphaned durable run on rejection.
        // Uses the already-authorized action (startable.Result) so a non-owner
        // marketplace run isn't rejected by owner-scoped dependency reads.
        var depGate = await EnforceDependenciesAsync(startable.Result, request);
        if (depGate is not null)
            return depGate;

        var actionResult = await _actionStore.GetActionAsync(actionId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = actionResult.Message };
        var action = actionResult.Result;

        // Marketplace guard 2 — economic-consent gate (durable path). Mirrors
        // ExecuteAsync: a non-owner run containing value-moving nodes is rejected
        // unless the runner acknowledged the disclosed effects. ValidateDAGAsync above
        // assigned ExecutionOrder so the manifest lists nodes in run order.
        var consentGate = EnforceEconomicConsent(action, isOwner, acknowledgeEconomicEffects);
        if (consentGate is not null)
            return consentGate;

        var entry = ResolveEntryNode(action);
        if (entry is null)
            return new AZOAResult<ActionRun> { IsError = true, Message = "Action has no entry node to start the workflow run." };

        // F6 TOCTOU guard: confirm the definition is STILL Active at the version we
        // read before committing a durable run -- same discipline as ExecuteAsync.
        // Closes unpublish racing a workflow-run-start on the durable path.
        var confirmed = await _actionStore.TryConfirmActionStateAsync(
            action.Id, ActionStatus.Active, action.Version);
        if (confirmed == 0)
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = "Action run conflict: the action was unpublished or modified concurrently. Reload and retry."
            };

        // Create the run + one Pending ActionNodeExecution per node — identical to
        // ExecuteAsync so the per-node claim/idempotency surface is shared.
        var run = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            // Stamp the run to the RUNNER (marketplace mechanic) — owner path: equal;
            // non-owner path: runner's own context with provenance back to origin.
            UserId = userId,
            // tenant-consent-delegation AC4: persist the acting tenant on the
            // durable run so it survives the async saga-worker hop and reaches the
            // Tier-2 node handlers via the ActionNodeExecutionContext. This is the
            // keystone — the durable path is where the acting tenant would otherwise
            // be lost (no ambient principal on the saga worker).
            ActingTenantId = actingTenantId,
            // Marketplace provenance: null on owner path, origin action + creator on
            // the non-owner path.
            SourceActionId = isOwner ? null : action.Id,
            OriginUserId = isOwner ? null : action.UserId,
            // Bind the run to the exact published graph revision (bait-and-switch guard).
            PublishedVersionHash = action.PublishedVersionHash,
            Status = ActionRunStatus.Pending,
            StartedAt = DateTime.UtcNow
        };
        var createRun = await _runStore.CreateAsync(run);
        if (createRun.IsError || createRun.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = createRun.Message };

        foreach (var node in action.Nodes)
        {
            var exec = new ActionNodeExecution
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                NodeId = node.Id,
                State = ActionNodeState.Pending,
                StartedAt = DateTime.UtcNow
            };
            var createExec = await _executionStore.CreateAsync(exec);
            if (createExec.IsError)
                return new AZOAResult<ActionRun> { IsError = true, Message = createExec.Message };
        }

        // Enqueue the entry node as the first saga step (step name = node id).
        // Started directly via the store, not the coordinator, because the
        // action-workflow definition is not a SagaDefinition (D1, Approach A).
        await EnqueueWorkflowNodeAsync(run.Id, action.Id, run.UserId, entry.Id, signalPayload: null);

        run.Status = ActionRunStatus.Running;
        var updated = await _runStore.UpdateAsync(run);
        return new AZOAResult<ActionRun> { Result = updated.Result ?? run, Message = "Workflow run started." };
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

        if (run.Status is not (ActionRunStatus.Suspended or ActionRunStatus.AwaitingSignal))
            return new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"Cannot advance run {runId}: status is {run.Status} (only Suspended/AwaitingSignal runs accept advance)."
            };

        var actionResult = await _actionStore.GetActionAsync(run.ActionId);
        if (actionResult.IsError || actionResult.Result == null)
            return new AZOAResult<ActionRun> { IsError = true, Message = actionResult.Message };
        var action = actionResult.Result;

        // Resolve the next hop through the SAME authority the engine's
        // auto-advance uses, so the manual and auto paths can never route the
        // same graph to different successors (or disagree on the fan-out guard).
        var hop = ActionWorkflowEdges.ResolveSingleSuccessor(action, fromNodeId);
        switch (hop.Kind)
        {
            case SuccessorKind.Terminal:
                // Manual-advance from a terminal node ⇒ the run completes.
                run.Status = ActionRunStatus.Succeeded;
                run.EndedAt = DateTime.UtcNow;
                var done = await _runStore.UpdateAsync(run);
                return new AZOAResult<ActionRun> { Result = done.Result ?? run, Message = "Workflow run completed (advanced past terminal node)." };

            case SuccessorKind.FanOut:
                return new AZOAResult<ActionRun>
                {
                    IsError = true,
                    Message = $"Cannot advance: node {fromNodeId} has {hop.Count} Control successors (fan-out is out of scope)."
                };

            default: // Single
                await EnqueueWorkflowNodeAsync(run.Id, run.ActionId, run.UserId, hop.NodeId!.Value, signalPayload: null);
                run.Status = ActionRunStatus.Running;
                var updated = await _runStore.UpdateAsync(run);
                return new AZOAResult<ActionRun> { Result = updated.Result ?? run, Message = "Workflow run advanced." };
        }
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

    /// <summary>
    /// Operator/background sweep: re-probe EVERY run parked in
    /// <see cref="ActionRunStatus.AwaitingReconciliation"/>. Unscoped (operator
    /// context). Each run runs through the same <see cref="ReprobeReconciliationAsync"/>
    /// chain-truth logic; a probe failure on one run does not abort the sweep.
    /// </summary>
    public async Task<AZOAResult<IEnumerable<ActionReconciliationResult>>> SweepReconciliationAsync(AZOARequest? request = null)
    {
        var parked = await _runStore.GetByStatusAsync(ActionRunStatus.AwaitingReconciliation);
        if (parked.IsError)
            return new AZOAResult<IEnumerable<ActionReconciliationResult>> { IsError = true, Message = parked.Message };

        var results = new List<ActionReconciliationResult>();
        foreach (var run in parked.Result ?? Enumerable.Empty<ActionRun>())
            results.Add(await ReprobeReconciliationAsync(run));

        return new AZOAResult<IEnumerable<ActionReconciliationResult>>
        {
            Result = results,
            Message = $"Swept {results.Count} run(s) awaiting reconciliation."
        };
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

    /// <summary>
    /// The shared reconcile-before-retry re-probe for ONE parked run. For each
    /// Failed chain-action execution that carries a broadcast tx hash, it re-probes
    /// chain truth and feeds the verdict into <see cref="ChainActionRecovery"/>:
    ///
    /// <list type="bullet">
    /// <item><b>Confirmed</b> → the tx LANDED. Reconcile the execution to Succeeded
    /// (guarded on Failed) and un-park the run's parked saga step (gate
    /// <c>recon:{nodeId}</c>) so the durable engine re-drives advancement from the
    /// now-Succeeded row — NO re-broadcast.</item>
    /// <item><b>FailedOnChain</b> → provably failed. Un-park the step; the
    /// re-dispatched node-step replays its Failed outcome into the saga's
    /// retry/compensation budget (re-broadcast is safe here).</item>
    /// <item><b>Pending/Unknown</b> (or a probe error / no hash) → still
    /// indeterminate. Leave the node Failed and the run parked, untouched, for the
    /// next sweep. NEVER auto-re-broadcast — this is the double-mint guard.</item>
    /// </list>
    ///
    /// <para>The run is left in <see cref="ActionRunStatus.AwaitingReconciliation"/>
    /// while ANY node remains indeterminate; once a node is un-parked the saga
    /// processor projects the run back to Running as it resumes. We never force a
    /// terminal status here — the engine (success advance) or the saga
    /// (retry/compensation) owns the terminal verdict, exactly as on the live path.</para>
    /// </summary>
    private async Task<ActionReconciliationResult> ReprobeReconciliationAsync(ActionRun run)
    {
        var outcome = new ActionReconciliationResult { RunId = run.Id, Status = run.Status };

        var execsResult = await _executionStore.GetByRunIdAsync(run.Id);
        var execs = (execsResult.Result ?? Enumerable.Empty<ActionNodeExecution>()).ToList();

        // Only Failed rows carrying a broadcast hash are candidates: those are the
        // parked chain-action nodes the step handler stamped before parking. A
        // Failed row with no hash was a non-broadcast failure (parked because we
        // could not prove anything was on the wire) — re-probing has nothing to
        // probe, so it stays parked for an operator.
        var candidates = execs
            .Where(e => e.State == ActionNodeState.Failed && !string.IsNullOrWhiteSpace(e.TxHash))
            .ToList();

        foreach (var exec in candidates)
        {
            var verdict = await ProbeConfirmationAsync(exec.TxHash, exec.ChainType);
            var action = ChainActionRecovery.Decide(exec.TxHash, verdict);

            switch (action)
            {
                case ChainActionRecoveryAction.AdvanceReconciled:
                    // Confirmed — reconcile the row to Succeeded (guarded on Failed),
                    // then un-park so the engine re-drives advancement. No re-mint.
                    exec.State = ActionNodeState.Succeeded;
                    exec.Error = null;
                    exec.EndedAt = DateTime.UtcNow;
                    var reconciled = await _executionStore.UpdateAsync(
                        exec, expectedState: ActionNodeState.Failed);
                    if (!reconciled.IsError)
                    {
                        await UnparkReconciliationStepAsync(run.Id, exec.NodeId);
                        outcome.ReconciledConfirmed++;
                    }
                    else
                    {
                        // The row drifted off Failed (a concurrent sweep already
                        // reconciled it) — count it as still-indeterminate for this
                        // pass rather than double-counting; the winner advanced it.
                        outcome.StillIndeterminate++;
                    }
                    break;

                case ChainActionRecoveryAction.Retry:
                    // Provably FailedOnChain — re-broadcast is safe. Un-park so the
                    // node-step replays Failed into the saga retry/compensation budget.
                    await UnparkReconciliationStepAsync(run.Id, exec.NodeId);
                    outcome.ReleasedFailedOnChain++;
                    break;

                case ChainActionRecoveryAction.ParkForReconciliation:
                default:
                    // Still in-flight / ambiguous — leave parked. NEVER re-broadcast.
                    outcome.StillIndeterminate++;
                    break;
            }
        }

        // Re-read the run so the caller sees the post-unpark status (the saga
        // projection flips AwaitingReconciliation→Running as the un-parked step
        // resumes). A run with no candidates, or all still-indeterminate, stays
        // AwaitingReconciliation.
        var refreshed = await _runStore.GetByIdAsync(run.Id);
        outcome.Status = refreshed.Result?.Status ?? run.Status;
        return outcome;
    }

    /// <summary>
    /// Re-probe chain truth for a recorded broadcast tx, mirroring the step
    /// handler's <c>ResolveProvider</c>/probe path. A missing hash, an unresolvable
    /// provider, or a probe error all fold to <see cref="ChainConfirmation.Unknown"/>
    /// — the conservative verdict that parks rather than re-broadcasts.
    /// </summary>
    private async Task<ChainConfirmation> ProbeConfirmationAsync(string? txHash, string? chainType)
    {
        if (string.IsNullOrWhiteSpace(txHash))
            return ChainConfirmation.Unknown;

        IBlockchainProvider? provider;
        try
        {
            provider = string.IsNullOrWhiteSpace(chainType)
                ? _chainFactory.GetDefaultProvider()
                : _chainFactory.GetProvider(chainType, _chainFactory.DefaultNetwork);
        }
        catch
        {
            return ChainConfirmation.Unknown;
        }

        var conf = await provider.GetTransactionConfirmationAsync(txHash!, CancellationToken.None);
        return conf.IsError ? ChainConfirmation.Unknown : conf.Result;
    }

    /// <summary>
    /// Un-park the saga step the step handler parked for a reconciliation node. The
    /// gate id mirrors the handler's park (<c>recon:{nodeId}</c>); a single-winner
    /// <c>TrySignalAsync</c> flips the parked step back to Pending so the processor
    /// re-dispatches it. Idempotent — a second un-park (a racing sweep) is a no-op.
    /// </summary>
    private async Task UnparkReconciliationStepAsync(Guid runId, Guid nodeId)
    {
        var reconGateId = $"recon:{nodeId}";
        await _sagaStore.TrySignalAsync(
            runId.ToString(), reconGateId, newPayloadJson: null, CancellationToken.None);
    }

    // ── Workflow helpers ───────────────────────────────────────────────────

    /// <summary>The entry node: the explicit <c>IsEntry</c> node, else the
    /// lowest <c>ExecutionOrder</c> node (the validator's topological head).</summary>
    private static ActionNode? ResolveEntryNode(ActionDefinition action)
    {
        var entry = action.Nodes.FirstOrDefault(n => n.IsEntry);
        if (entry is not null)
            return entry;
        return action.Nodes.OrderBy(n => n.ExecutionOrder).FirstOrDefault();
    }

    /// <summary>Enqueue an action node as a saga step (the first step of a run, or
    /// a manual/signal-driven resume). Mirrors the handler's self-advance enqueue
    /// so the correlation key, step name, and idempotency key are derived
    /// identically.</summary>
    private async Task EnqueueWorkflowNodeAsync(
        Guid runId, Guid actionId, Guid userId, Guid nodeId, string? signalPayload)
    {
        var payload = new ActionStepPayload(runId, actionId, userId, nodeId, signalPayload);
        var stepName = nodeId.ToString();
        var idemKey = SagaKeys.StepIdempotencyKey(runId.ToString(), stepName);
        await _sagaStore.EnqueueNextStepAsync(
            ActionWorkflowSaga.Name, stepName, runId.ToString(),
            idemKey, SagaStep<ActionStepPayload>.Serialize(payload), CancellationToken.None);
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
