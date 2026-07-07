using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Sagas;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Predicates;
using AZOA.WebAPI.Services.Quest.Workflow;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Orchestrates quest execution against the per-run / per-(run,node) runtime
/// surface introduced by the <c>quest-temporal-fork-model</c> track. Holds a
/// <see cref="IQuestStore"/> (definitions, immutable in-flight),
/// <see cref="IQuestRunStore"/> (one row per execution attempt),
/// <see cref="IQuestNodeExecutionStore"/> (per-(run, node) state/output/error),
/// and <see cref="IQuestNodeHandlerRegistry"/> (the per-<see cref="QuestNodeType"/>
/// dispatch table).
/// </summary>
public class QuestManager : IQuestManager
{
    private readonly IQuestStore _questStore;
    private readonly IQuestRunStore _runStore;
    private readonly IQuestNodeExecutionStore _executionStore;
    private readonly IQuestDagValidator _dagValidator;
    private readonly IQuestDagExecutabilityValidator _executabilityValidator;
    private readonly IQuestNodeHandlerRegistry _registry;
    private readonly ISagaStore _sagaStore;
    private readonly IWalletManager _walletManager;
    private readonly IBlockchainProviderFactory _chainFactory;
    private readonly QuestConfigBindingResolver _bindingResolver;

    // Config-bound marketplace guards (see Managers/AGENTS.md §quest-run-quota
    // and §economic-consent). Materialized once from IConfiguration at construction.
    private readonly QuestRunQuotaOptions _runQuota;

    // Semantic transition-legality layer (smart-gates-holon-state §8.2), invoked
    // ALONGSIDE the structural Kahn validator — never replacing it. Stateless with
    // the default project-lifecycle map; constructing it here keeps the DI signature
    // unchanged. A future track that needs a per-quest lifecycle can promote this to
    // an injected dependency.
    private readonly QuestTransitionValidator _transitionValidator = new();

    public QuestManager(
        IQuestStore questStore,
        IQuestRunStore runStore,
        IQuestNodeExecutionStore executionStore,
        IQuestDagValidator dagValidator,
        IQuestDagExecutabilityValidator executabilityValidator,
        IQuestNodeHandlerRegistry registry,
        ISagaStore sagaStore,
        IWalletManager walletManager,
        IBlockchainProviderFactory chainFactory,
        QuestConfigBindingResolver bindingResolver,
        // Optional so the ~10 unit-test fixtures that construct QuestManager with the
        // 10 positional deps keep compiling; DI always supplies the real config in
        // production. Null ⇒ default QuestRunQuotaOptions. See §quest-run-quota.
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        _questStore = questStore;
        _runStore = runStore;
        _executionStore = executionStore;
        _dagValidator = dagValidator;
        _executabilityValidator = executabilityValidator;
        _registry = registry;
        _sagaStore = sagaStore;
        _walletManager = walletManager;
        _chainFactory = chainFactory;
        _bindingResolver = bindingResolver;
        _runQuota = configuration is null
            ? new QuestRunQuotaOptions()
            : QuestRunQuotaOptions.FromConfiguration(configuration);
    }

    /// <summary>
    /// Loads a quest and rejects when it is owned by a different avatar. Returns
    /// the loaded quest on success, or an error result to surface verbatim.
    /// </summary>
    private async Task<AZOAResult<Quest>> LoadOwnedQuestAsync(Guid questId, Guid avatarId)
    {
        var questResult = await _questStore.GetQuestAsync(questId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<Quest> { IsError = true, Message = questResult.Message ?? "Quest not found." };
        if (questResult.Result.AvatarId != avatarId)
            return new AZOAResult<Quest> { IsError = true, Message = "Quest is owned by a different avatar." };
        return questResult;
    }

    /// <summary>
    /// Loads a quest for a run-start (marketplace mechanic): the OWNER may start
    /// any of their own quests; a NON-owner may start it only when it is published
    /// (<see cref="Quest.IsPublic"/>) AND Active. Returns the quest and whether the
    /// caller is the owner, or an error result to surface verbatim. Private quests
    /// stay owner-only; a non-owner sees the same "not found"-style rejection to
    /// avoid leaking existence of private quests.
    /// See Managers/AGENTS.md §publish-lifecycle.
    /// </summary>
    private async Task<(AZOAResult<Quest> result, bool isOwner)> LoadStartableQuestAsync(Guid questId, Guid avatarId)
    {
        var questResult = await _questStore.GetQuestAsync(questId);
        if (questResult.IsError || questResult.Result == null)
            return (new AZOAResult<Quest> { IsError = true, Message = questResult.Message ?? "Quest not found." }, false);

        var quest = questResult.Result;
        if (quest.AvatarId == avatarId)
            return (questResult, true);

        // Non-owner path: only published (public + Active) quests are startable.
        if (!quest.IsPublic)
            return (new AZOAResult<Quest> { IsError = true, Message = "Quest not found." }, false);
        if (quest.Status != QuestStatus.Active)
            return (new AZOAResult<Quest> { IsError = true, Message = "Quest is not published for execution." }, false);

        return (questResult, false);
    }

    // ═══════════════════════════════════════════════════════════════════
    // MARKETPLACE RUN-START GUARDS (per-(avatar,quest) quota + economic consent).
    // Both run-start seams (ExecuteAsync + StartWorkflowRunAsync) call these BEFORE
    // any run/exec rows are written — a breach rejects cleanly with no orphaned run.
    // See Managers/AGENTS.md §quest-run-quota and §economic-consent.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-(avatar, quest) run-start quota guard (treasury/runner drain). Rejects a
    /// new start when the avatar already holds the configured max NON-terminal runs
    /// of this quest. Config-driven; owner gets a higher (or unbounded) ceiling.
    /// Returns an error result on breach, or null to proceed. See §quest-run-quota.
    /// </summary>
    private async Task<AZOAResult<QuestRun>?> EnforceRunQuotaAsync(Guid questId, Guid avatarId, bool isOwner)
    {
        var limit = _runQuota.EffectiveLimit(isOwner);
        if (limit is null)
            return null; // quota disabled / caller exempt

        // Count this avatar's non-terminal runs of THIS quest from the run store.
        var runsResult = await _runStore.GetByQuestIdAsync(questId);
        if (runsResult.IsError)
            // Fail closed: we cannot prove the avatar is under quota, so reject.
            return new AZOAResult<QuestRun>
            {
                IsError = true,
                Message = $"Run-start quota check failed; run rejected (fail-closed): {runsResult.Message}"
            };

        var active = (runsResult.Result ?? Enumerable.Empty<QuestRun>())
            .Count(r => r.AvatarId == avatarId && !r.Status.IsTerminal());

        if (active >= limit.Value)
            return new AZOAResult<QuestRun>
            {
                IsError = true,
                Message = $"Run-start quota exceeded: avatar already has {active} active run(s) of quest {questId} " +
                          $"(limit {limit.Value}). Let existing runs reach a terminal state before starting another."
            };

        return null;
    }

    /// <summary>
    /// Economic-consent gate for a NON-owner (marketplace) run. Computes the
    /// value-moving-node manifest and, when any economic node is present, requires
    /// <paramref name="acknowledgeEconomicEffects"/> to be true. Owner runs are
    /// exempt (they authored the quest). Returns an error on an un-acknowledged
    /// economic run, or null to proceed. See §economic-consent.
    /// </summary>
    private AZOAResult<QuestRun>? EnforceEconomicConsent(Quest quest, bool isOwner, bool acknowledgeEconomicEffects)
    {
        if (isOwner)
            return null; // owner authored the quest — no external disclosure needed

        var manifest = QuestEconomicManifestBuilder.Build(quest, _registry, quest.PublishedVersionHash);
        if (!manifest.HasEconomicNodes)
            return null; // nothing moves value — no consent required

        if (acknowledgeEconomicEffects)
            return null; // runner explicitly acknowledged the disclosed effects

        var summary = string.Join(", ", manifest.Entries.Select(e => $"{e.NodeType} '{e.NodeName}'"));
        return new AZOAResult<QuestRun>
        {
            IsError = true,
            Message = "This quest moves assets on your behalf and requires explicit consent. " +
                      $"Value-moving nodes: [{summary}]. Preview them via the run-preview surface, " +
                      "then re-start with acknowledgeEconomicEffects=true to proceed."
        };
    }

    /// <summary>
    /// Loads a run and rejects when it is owned by a different avatar. Returns
    /// the loaded run on success, or an error result to surface verbatim.
    /// </summary>
    private async Task<AZOAResult<QuestRun>> LoadOwnedRunAsync(Guid runId, Guid avatarId)
    {
        var runResult = await _runStore.GetByIdAsync(runId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = runResult.Message ?? "Quest run not found." };
        if (runResult.Result.AvatarId != avatarId)
            return new AZOAResult<QuestRun> { IsError = true, Message = "Quest run is owned by a different avatar." };
        return runResult;
    }

    // ═══════════════════════════════════════════════════════════════════
    // QUEST CRUD
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<Quest>> CreateAsync(QuestCreateModel model, Guid avatarId, AZOARequest? request = null)
    {
        var quest = new Quest
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            Description = model.Description,
            AvatarId = avatarId,
            IsPublic = model.IsPublic,
            CreatedDate = DateTime.UtcNow
        };

        // Create nodes with new Ids
        var nodeIds = new List<Guid>();
        foreach (var nodeModel in model.Nodes)
        {
            var node = new QuestNode
            {
                Id = Guid.NewGuid(),
                QuestId = quest.Id,
                Name = nodeModel.Name,
                NodeType = nodeModel.NodeType,
                Config = nodeModel.Config,
                IsEntry = nodeModel.IsEntry,
                IsTerminal = nodeModel.IsTerminal,
                NodeTemplateId = nodeModel.NodeTemplateId
            };
            quest.Nodes.Add(node);
            nodeIds.Add(node.Id);
        }

        // Map edge indices to node Ids
        foreach (var edgeModel in model.Edges)
        {
            if (edgeModel.SourceNodeId < 0 || edgeModel.SourceNodeId >= nodeIds.Count ||
                edgeModel.TargetNodeId < 0 || edgeModel.TargetNodeId >= nodeIds.Count)
            {
                return new AZOAResult<Quest> { IsError = true, Message = $"Edge index out of range. Source={edgeModel.SourceNodeId}, Target={edgeModel.TargetNodeId}, NodeCount={nodeIds.Count}." };
            }

            var edge = new QuestEdge
            {
                Id = Guid.NewGuid(),
                QuestId = quest.Id,
                SourceNodeId = nodeIds[edgeModel.SourceNodeId],
                TargetNodeId = nodeIds[edgeModel.TargetNodeId],
                Condition = edgeModel.Condition,
                EdgeType = edgeModel.EdgeType
            };
            quest.Edges.Add(edge);
        }

        return await _questStore.UpsertQuestAsync(quest);
    }

    public async Task<AZOAResult<Quest>> GetAsync(Guid id, Guid avatarId, AZOARequest? request = null)
    {
        // Owner may always read; a non-owner may read a PUBLIC quest (read-only) so
        // it can be fetched and started under their own context. Private quests stay
        // owner-only and surface the same "different avatar" rejection.
        var questResult = await _questStore.GetQuestAsync(id);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<Quest> { IsError = true, Message = questResult.Message ?? "Quest not found." };
        var quest = questResult.Result;
        if (quest.AvatarId == avatarId || quest.IsPublic)
            return questResult;
        return new AZOAResult<Quest> { IsError = true, Message = "Quest is owned by a different avatar." };
    }

    /// <summary>
    /// Marketplace discovery: quests any avatar may fork/start — public AND published
    /// (Active). The browse surface for the start-as-template mechanic; without it a
    /// public quest is only reachable if its id was shared out-of-band. Mirrors the
    /// non-owner gate in <see cref="LoadStartableQuestAsync"/> (IsPublic && Active).
    /// </summary>
    public async Task<AZOAResult<IEnumerable<Quest>>> ListPublicAsync(AZOARequest? request = null)
    {
        var all = await _questStore.GetPublicQuestsAsync();
        if (all.IsError || all.Result == null) return all;
        var published = all.Result.Where(q => q.Status == QuestStatus.Active);
        return new AZOAResult<IEnumerable<Quest>> { Result = published, Message = "Public quests." };
    }

    public async Task<AZOAResult<IEnumerable<Quest>>> GetByAvatarAsync(Guid avatarId, AZOARequest? request = null)
    {
        return await _questStore.GetQuestsByAvatarAsync(avatarId);
    }

    public async Task<AZOAResult<Quest>> UpdateAsync(Guid id, QuestUpdateModel model, Guid avatarId, AZOARequest? request = null)
    {
        var existing = await LoadOwnedQuestAsync(id, avatarId);
        if (existing.IsError || existing.Result == null) return existing;

        var quest = existing.Result;
        if (model.Name != null) quest.Name = model.Name;
        if (model.Description != null) quest.Description = model.Description;
        // Owner-only visibility toggle (LoadOwnedQuestAsync above already enforced
        // ownership → IDOR-safe). Null leaves the flag unchanged.
        if (model.IsPublic.HasValue) quest.IsPublic = model.IsPublic.Value;
        // model.Status is intentionally ignored — runtime status moved to
        // QuestRun.Status (see ADR §2.2). The field on QuestUpdateModel is
        // retained for API back-compat but has no effect on the definition.

        return await _questStore.UpsertQuestAsync(quest);
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid avatarId, AZOARequest? request = null)
    {
        var owned = await LoadOwnedQuestAsync(id, avatarId);
        if (owned.IsError || owned.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = owned.Message };

        return await _questStore.DeleteQuestAsync(id);
    }

    // ═══════════════════════════════════════════════════════════════════
    // DEFINITION LIFECYCLE — publish / unpublish (FR-2)
    // See Managers/AGENTS.md §publish-lifecycle.
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<Quest>> PublishAsync(Guid questId, Guid avatarId, AZOARequest? request = null)
    {
        var owned = await LoadOwnedQuestAsync(questId, avatarId);
        if (owned.IsError || owned.Result == null)
            return owned;
        var quest = owned.Result;

        if (quest.Status == QuestStatus.Active)
            return new AZOAResult<Quest> { IsError = true, Message = "Quest is already Active." };

        // Full validation stack: structural DAG + fan-out-as-error + config check.
        var dagResult = _dagValidator.Validate(quest, fanOutAsError: true);
        if (!dagResult.IsValid)
            return new AZOAResult<Quest> { IsError = true, Message = $"Publish failed — DAG invalid: {string.Join("; ", dagResult.Errors)}" };

        var transitionResult = _transitionValidator.Validate(quest);
        if (!transitionResult.IsValid)
            return new AZOAResult<Quest> { IsError = true, Message = $"Publish failed — transition invalid: {string.Join("; ", transitionResult.Errors)}" };

        // Per-node config check (hook filled in by Phase C / QuestNodeConfigRegistry).
        var configError = ValidateNodeConfigs(quest);
        if (configError != null)
            return new AZOAResult<Quest> { IsError = true, Message = $"Publish failed — node config invalid: {configError}" };

        // Executability gate: reject if any $from binding input won't be satisfiable
        // at runtime (unreachable source, absent output field, provable scalar type
        // mismatch). Runs LAST so structural DAG / transition / config errors surface
        // first. See Services/Quest/AGENTS.md §executability-validation.
        var execResult = _executabilityValidator.Validate(quest);
        if (!execResult.IsValid)
            return new AZOAResult<Quest> { IsError = true, Message = $"Publish failed — binding not executable: {string.Join("; ", execResult.Errors)}" };

        // F6 TOCTOU guard: flip Draft → Active via compare-and-swap on the version
        // we read above. This is the serialization point — two concurrent publishes
        // race here and exactly one wins; the loser sees affected==0 (the row already
        // advanced) and gets a conflict, never a torn double-publish.
        var expectedVersion = quest.Version;
        var won = await _questStore.TryTransitionQuestStatusAsync(
            questId, QuestStatus.Draft, QuestStatus.Active, expectedVersion);
        if (won == 0)
            return new AZOAResult<Quest>
            {
                IsError = true,
                Message = "Publish conflict: the quest was modified or published concurrently. Reload and retry."
            };

        // Persist fresh ExecutionOrder (dagResult already populated it via Validate).
        // Safe now: we own the Active transition, so no concurrent publisher can
        // interleave a graph write. Assign the authoritative post-CAS row shape
        // (status Active, version bumped once) so the CONTENT upsert round-trips it
        // exactly — assignment (not +=) is reference-sharing-safe for the in-memory
        // store, whose CAS mutated the same instance.
        quest.Status  = QuestStatus.Active;
        quest.Version = expectedVersion + 1;
        // Bait-and-switch guard: snapshot a stable content hash of the node/edge
        // graph being published. Runs bind to this exact revision; a later
        // unpublish→edit→republish recomputes it, so a mismatch is detectable.
        // See Managers/AGENTS.md §published-version-hash.
        quest.PublishedVersionHash = QuestPublishedVersion.ComputeHash(quest);
        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new AZOAResult<Quest> { IsError = true, Message = upsert.Message };

        return new AZOAResult<Quest> { Result = upsert.Result, Message = "Quest published." };
    }

    public async Task<AZOAResult<Quest>> UnpublishAsync(Guid questId, Guid avatarId, AZOARequest? request = null)
    {
        var owned = await LoadOwnedQuestAsync(questId, avatarId);
        if (owned.IsError || owned.Result == null)
            return owned;
        var quest = owned.Result;

        if (quest.Status != QuestStatus.Active)
            return new AZOAResult<Quest> { IsError = true, Message = "Quest is not Active; cannot unpublish." };

        // Refuse while any in-flight run exists (AC-2d).
        var runsResult = await _runStore.GetByQuestIdAsync(questId);
        if (!runsResult.IsError && runsResult.Result != null)
        {
            var inFlight = runsResult.Result.Where(r => !r.Status.IsTerminal()).ToList();
            if (inFlight.Count > 0)
                return new AZOAResult<Quest>
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
        var expectedVersion = quest.Version;
        var won = await _questStore.TryTransitionQuestStatusAsync(
            questId, QuestStatus.Active, QuestStatus.Draft, expectedVersion);
        if (won == 0)
            return new AZOAResult<Quest>
            {
                IsError = true,
                Message = "Unpublish conflict: the quest was modified or transitioned concurrently. Reload and retry."
            };

        // Reflect the authoritative post-CAS shape (assignment, not +=, is
        // reference-sharing-safe for the in-memory store's in-place CAS).
        quest.Status  = QuestStatus.Draft;
        quest.Version = expectedVersion + 1;
        return new AZOAResult<Quest> { Result = quest, Message = "Quest unpublished." };
    }

    /// <summary>
    /// Per-node config validation at publish time: runs the binding pre-pass
    /// with the node's direct upstream names (so upstream.&lt;name&gt; paths are
    /// checked against the actual incoming edges) plus the strict shadow round-trip.
    /// Returns the first error message, or null when all nodes pass.
    /// </summary>
    private string? ValidateNodeConfigs(Quest quest)
    {
        // run.<name> is run-scoped (any node in the quest), not edge-scoped, so
        // it validates against the full node-name set rather than direct upstreams.
        var allNodeNames = quest.Nodes.Select(n => n.Name).ToHashSet();

        foreach (var node in quest.Nodes)
        {
            // Build the set of direct upstream node names (sources of incoming edges).
            var directUpstreamNames = quest.Edges
                .Where(e => e.TargetNodeId == node.Id)
                .Select(e => quest.Nodes.FirstOrDefault(n => n.Id == e.SourceNodeId)?.Name)
                .Where(n => n is not null)
                .Cast<string>()
                .ToHashSet();

            var err = QuestNodeConfigRegistry.Validate(node.NodeType, node.Config, directUpstreamNames, allNodeNames);
            if (err != null)
                return $"Node '{node.Name}' ({node.NodeType}): {err}";
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    // DAG VALIDATION
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<bool>> ValidateDAGAsync(Guid questId, AZOARequest? request = null)
    {
        var questResult = await _questStore.GetQuestAsync(questId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;

        // QuestDagValidator is the single ExecutionOrder authority — Validate
        // mutates node.ExecutionOrder in-place on the quest graph.
        var validation = _dagValidator.Validate(quest);

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
        var transitionValidation = _transitionValidator.Validate(quest);
        if (!transitionValidation.IsValid)
        {
            return new AZOAResult<bool>
            {
                IsError = true,
                Result = false,
                Message = $"DAG validation failed: {string.Join("; ", transitionValidation.Errors)}"
            };
        }

        await _questStore.UpsertQuestAsync(quest);

        return new AZOAResult<bool> { Result = true, Message = "DAG is valid." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // EXECUTION
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<QuestRun>> ExecuteAsync(Guid questId, Guid avatarId, AZOARequest? request = null, Guid? actingTenantId = null, bool acknowledgeEconomicEffects = false)
    {
        // Marketplace mechanic: the owner may run their own quest; a non-owner may
        // run it only when it is published (public + Active). Provenance back to the
        // origin quest/creator is stamped on the run below for the non-owner path.
        var (startable, isOwner) = await LoadStartableQuestAsync(questId, avatarId);
        if (startable.IsError || startable.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = startable.Message };

        // Marketplace guard 1 — per-(avatar,quest) run-start quota (treasury/runner drain).
        var quotaGate = await EnforceRunQuotaAsync(questId, avatarId, isOwner);
        if (quotaGate is not null)
            return quotaGate;

        // FR-2 (quest-dag-semantic-hardening): require Active status before execution.
        // (The non-owner path already enforced Active in LoadStartableQuestAsync; this
        // keeps the owner path's original gate + error message intact.)
        if (startable.Result.Status != QuestStatus.Active)
            return new AZOAResult<QuestRun> { IsError = true, Message = "Quest must be published (Active) before it can be executed. Call POST /{id}/publish first." };

        // Validate DAG first (also assigns ExecutionOrder onto definition nodes).
        var validationResult = await ValidateDAGAsync(questId, request);
        if (validationResult.IsError)
            return new AZOAResult<QuestRun> { IsError = true, Message = validationResult.Message };

        // final-hardening F4: enforce quest dependencies at run start (fail-closed).
        // Rejected BEFORE any run/exec rows are written — no orphaned run on rejection.
        var depGate = await EnforceDependenciesAsync(questId, avatarId, request);
        if (depGate is not null)
            return depGate;

        var questResult = await _questStore.GetQuestAsync(questId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;

        // Marketplace guard 2 — economic-consent gate. DAG validation above assigned
        // ExecutionOrder, so the manifest lists value-moving nodes in run order. A
        // non-owner run containing economic nodes is rejected unless acknowledged.
        var consentGate = EnforceEconomicConsent(quest, isOwner, acknowledgeEconomicEffects);
        if (consentGate is not null)
            return consentGate;

        // F6 TOCTOU guard: confirm the definition is STILL Active at the version we
        // just read before we commit a run against it. This closes unpublish racing
        // a run-start — if an UnpublishAsync flipped Active → Draft (bumping version)
        // between our ownership read and here, the confirm misses and we reject
        // rather than executing a torn/unpublished definition.
        var confirmed = await _questStore.TryConfirmQuestStateAsync(
            quest.Id, QuestStatus.Active, quest.Version);
        if (confirmed == 0)
            return new AZOAResult<QuestRun>
            {
                IsError = true,
                Message = "Quest run conflict: the quest was unpublished or modified concurrently. Reload and retry."
            };

        // Create the QuestRun upfront — Pending → Running on first node claim.
        // The run is ALWAYS stamped to the RUNNER (avatarId), not the quest owner —
        // on the owner path they are equal; on the non-owner (marketplace) path the
        // runner gets a run under their own context with provenance back to origin.
        var run = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = avatarId,
            // tenant-consent-delegation AC4: stamp the acting tenant from the
            // activating principal so Tier-2 nodes carry it to the signing seam.
            ActingTenantId = actingTenantId,
            // Marketplace provenance: null on the owner path, origin quest + creator
            // on the non-owner path.
            SourceQuestId = isOwner ? null : quest.Id,
            OriginAvatarId = isOwner ? null : quest.AvatarId,
            // Bind the run to the exact published graph revision (bait-and-switch guard).
            PublishedVersionHash = quest.PublishedVersionHash,
            Status = QuestRunStatus.Pending,
            StartedAt = DateTime.UtcNow
        };
        var createRun = await _runStore.CreateAsync(run);
        if (createRun.IsError || createRun.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = createRun.Message };

        // Pre-create one QuestNodeExecution(Pending) per quest node.
        foreach (var node in quest.Nodes)
        {
            var exec = new QuestNodeExecution
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                NodeId = node.Id,
                State = QuestNodeState.Pending,
                StartedAt = DateTime.UtcNow
            };
            var createExec = await _executionStore.CreateAsync(exec);
            if (createExec.IsError)
                return new AZOAResult<QuestRun> { IsError = true, Message = createExec.Message };
        }

        // Status: Pending → Running (first claim).
        run.Status = QuestRunStatus.Running;
        await _runStore.UpdateAsync(run);

        // Track per-node executions in-memory for ComposeOutputs upstream lookup.
        var executionsByNode = new Dictionary<Guid, QuestNodeExecution>();

        // Execute nodes in topological order.
        var sortedNodes = quest.Nodes.OrderBy(n => n.ExecutionOrder).ToList();

        foreach (var node in sortedNodes)
        {
            // V2 skip rule (Managers/AGENTS.md §onfailure-semantics):
            // 1. Any Control/Conditional source Failed or Skipped → skip.
            // 2. OnFailure edges are excluded from rule 1; they activate when
            //    source Failed and skip when source Succeeded/Skipped (inverse).
            // 3. When ≥1 OnFailure edges exist: node runs only if ≥1 OnFailure
            //    source Failed (and no Control/Conditional source failed rule 1).
            var incomingEdges = quest.Edges.Where(e => e.TargetNodeId == node.Id).ToList();
            var shouldSkip = false;

            var onFailureEdges = incomingEdges.Where(e => e.EdgeType == QuestEdgeType.OnFailure).ToList();
            var controlEdges = incomingEdges.Where(
                e => e.EdgeType == QuestEdgeType.Control || e.EdgeType == QuestEdgeType.Conditional).ToList();

            // Rule 1: Control/Conditional source Failed or Skipped → skip.
            foreach (var edge in controlEdges)
            {
                executionsByNode.TryGetValue(edge.SourceNodeId, out var sourceExec);
                var sourceState = sourceExec?.State;

                if (edge.EdgeType == QuestEdgeType.Conditional)
                {
                    if (sourceState == QuestNodeState.Failed || sourceState == QuestNodeState.Skipped)
                    {
                        shouldSkip = true;
                        break;
                    }
                }

                if (edge.EdgeType == QuestEdgeType.Control &&
                    (sourceState == QuestNodeState.Failed || sourceState == QuestNodeState.Skipped))
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
                    return ex?.State == QuestNodeState.Failed;
                });
                // Skip unless at least one OnFailure source actually Failed.
                if (!anyOnFailureSourceFailed)
                    shouldSkip = true;
            }

            var execution = (await _executionStore.GetByRunAndNodeAsync(run.Id, node.Id)).Result!;

            if (shouldSkip)
            {
                execution.State = QuestNodeState.Skipped;
                execution.EndedAt = DateTime.UtcNow;
                // HIGH#7 G2 guard: only mark Skipped if the row is still
                // Pending. If a concurrent ForkAsync flipped it to Cancelled
                // we must not silently overwrite — the store returns an
                // error result and we simply re-read the latest state for
                // downstream ComposeOutputs lookup.
                var skipUpdate = await _executionStore.UpdateAsync(
                    execution, expectedState: QuestNodeState.Pending);
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
                execution.State = QuestNodeState.Failed;
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
            var upstream = new Dictionary<Guid, QuestNodeExecution>();
            foreach (var edge in incomingEdges)
            {
                if (executionsByNode.TryGetValue(edge.SourceNodeId, out var pe))
                    upstream[edge.SourceNodeId] = pe;
            }

            // FR-1 ($from binding) — resolve before handler dispatch.
            // See Services/Quest/AGENTS.md §output-binding.
            // C1/H1: identity side-effects and holon-scoped $from binding resolve
            // against the RUNNER (run.AvatarId), never the quest owner. On the owner
            // path run.AvatarId == quest.AvatarId, so behaviour is unchanged.
            var bindingResult = await _bindingResolver.TryResolveAsync(
                node.Config, node, quest, upstream, executionsByNode, run.AvatarId, CancellationToken.None);

            QuestNodeHandlerResult result;
            if (!bindingResult.Ok)
            {
                result = QuestNodeResults.Fail($"$from binding error on node '{node.Name}': {bindingResult.Error}");
            }
            else if (!_registry.TryGet(node.NodeType, out var handler))
            {
                result = QuestNodeResults.Fail($"Unsupported node type: {node.NodeType}");
            }
            else if (handler.RequiresChainCapability
                && !await ChainCapabilityGate.HasWalletBoundAsync(_walletManager, run.AvatarId))
            {
                // D1 pre-execution capability gate — fails closed (no broadcast):
                // a chain-requiring node may not run unless the actor has a wallet
                // bound. HandleAsync is SKIPPED entirely.
                result = QuestNodeResults.Fail(ChainCapabilityGate.NoWalletBoundMessage);
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
                        var ctx = new QuestNodeExecutionContext(run.Id, node.Id, quest, run.AvatarId, upstream, run.ActingTenantId, executionsByNode);
                        result = await handler.HandleAsync(ctx);
                    }
                    finally
                    {
                        node.Config = originalConfig;
                    }
                }
                catch (Exception ex)
                {
                    result = QuestNodeResults.Fail(ex.Message);
                }
            }

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
            // HIGH#7 — only commit the terminal state when the row is still
            // Running. A concurrent fork (or supervisor-fail) may have
            // flipped the row to Cancelled between our claim and our
            // completion; in that case the guard rejects the overwrite and
            // we accept the fork's decision rather than resurrecting the
            // execution into Succeeded/Failed.
            var terminalUpdate = await _executionStore.UpdateAsync(
                execution, expectedState: QuestNodeState.Running);
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
        var allExecs = (await _executionStore.GetByRunIdAsync(run.Id)).Result ?? Enumerable.Empty<QuestNodeExecution>();
        var nodesWithOnFailureOut = quest.Edges
            .Where(e => e.EdgeType == QuestEdgeType.OnFailure)
            .Select(e => e.SourceNodeId)
            .ToHashSet();
        var hasUnhandledFailure = allExecs.Any(e =>
            e.State == QuestNodeState.Failed && !nodesWithOnFailureOut.Contains(e.NodeId));
        run.Status = hasUnhandledFailure ? QuestRunStatus.Failed : QuestRunStatus.Succeeded;
        run.EndedAt = DateTime.UtcNow;
        var updated = await _runStore.UpdateAsync(run);

        return new AZOAResult<QuestRun> { Result = updated.Result, Message = $"Quest run {run.Status}." };
    }

    public async Task<AZOAResult<QuestNodeExecution>> ExecuteNodeAsync(Guid questId, Guid nodeId, Guid avatarId, AZOARequest? request = null, Guid? actingTenantId = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<QuestNodeExecution> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        var node = quest.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return new AZOAResult<QuestNodeExecution> { IsError = true, Message = "Node not found." };

        // Single-node execution creates an ad-hoc QuestRun that lives just long
        // enough to record this one node's outcome. This preserves the
        // historic ExecuteNodeAsync entry point (used by the QuestController)
        // while keeping the new (runId, nodeId) invariant: no state ever lands
        // on the QuestNode itself.
        var run = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = quest.AvatarId,
            // tenant-consent-delegation AC4: carry the acting tenant onto the
            // ad-hoc one-node run so the dispatched handler reaches the seam.
            ActingTenantId = actingTenantId,
            Status = QuestRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await _runStore.CreateAsync(run);

        var execution = new QuestNodeExecution
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            NodeId = node.Id,
            State = QuestNodeState.Running,
            StartedAt = DateTime.UtcNow
        };
        await _executionStore.CreateAsync(execution);

        // FR-1 ($from binding) — resolve before handler dispatch (single-node path).
        // Single-node path is owner-only (LoadOwnedQuestAsync), so run.AvatarId ==
        // quest.AvatarId here; use run.AvatarId for parity with the multi-node path.
        var bindingResultSingle = await _bindingResolver.TryResolveAsync(
            node.Config, node, quest, new Dictionary<Guid, QuestNodeExecution>(),
            new Dictionary<Guid, QuestNodeExecution>(), run.AvatarId, CancellationToken.None);

        QuestNodeHandlerResult result;
        if (!bindingResultSingle.Ok)
        {
            result = QuestNodeResults.Fail($"$from binding error on node '{node.Name}': {bindingResultSingle.Error}");
        }
        else if (!_registry.TryGet(node.NodeType, out var handler))
        {
            result = QuestNodeResults.Fail($"Unsupported node type: {node.NodeType}");
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
                    var ctx = new QuestNodeExecutionContext(run.Id, node.Id, quest, run.AvatarId, upstreamExecutions: null, actingTenantId: run.ActingTenantId);
                    result = await handler.HandleAsync(ctx);
                }
                finally
                {
                    node.Config = originalConfigSingle;
                }
            }
            catch (Exception ex)
            {
                result = QuestNodeResults.Fail(ex.Message);
            }
        }

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
        // HIGH#7 — same G2 guard as the in-loop terminal update at line ~308.
        // The ad-hoc single-node run created the execution in Running state,
        // so we only commit Succeeded/Failed when the row is still Running.
        await _executionStore.UpdateAsync(execution, expectedState: QuestNodeState.Running);

        run.Status = result.IsError ? QuestRunStatus.Failed : QuestRunStatus.Succeeded;
        run.EndedAt = DateTime.UtcNow;
        await _runStore.UpdateAsync(run);

        return result.IsError
            ? new AZOAResult<QuestNodeExecution> { IsError = true, Result = execution, Message = result.Message ?? string.Empty }
            : new AZOAResult<QuestNodeExecution> { Result = execution, Message = "Node executed successfully." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // FORK
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<QuestRun>> ForkAsync(Guid runId, Guid atNodeId, string reason, Guid avatarId, AZOARequest? request = null)
    {
        var parentResult = await LoadOwnedRunAsync(runId, avatarId);
        if (parentResult.IsError || parentResult.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = parentResult.Message };

        var parent = parentResult.Result;

        // State-machine guard: only Running runs are forkable. Succeeded runs
        // are re-runnable (new root) but not forkable; Failed/Forked/Cancelled
        // are terminal. See ADR §2.3.
        if (parent.Status != QuestRunStatus.Running)
        {
            return new AZOAResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot fork run {runId}: status is {parent.Status} (only Running runs are forkable)."
            };
        }

        var questResult = await _questStore.GetQuestAsync(parent.QuestId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        var forkPointNode = quest.Nodes.FirstOrDefault(n => n.Id == atNodeId);
        if (forkPointNode == null)
        {
            return new AZOAResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot fork at node {atNodeId}: not present in quest {parent.QuestId} definition."
            };
        }
        var forkPoint = forkPointNode.ExecutionOrder;

        // Create the child run with lineage fields populated.
        var child = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = parent.QuestId,
            AvatarId = parent.AvatarId,
            // tenant-consent-delegation AC4: a tenant-driven run stays tenant-driven
            // across forks — inherit the parent run's acting tenant (null = user-driven)
            // rather than re-reading a principal, since the fork has no fresh JWT context.
            ActingTenantId = parent.ActingTenantId,
            Status = QuestRunStatus.Pending,
            StartedAt = DateTime.UtcNow,
            ParentRunId = parent.Id,
            ForkedAtNodeId = atNodeId,
            ForkReason = reason
        };
        var createChild = await _runStore.CreateAsync(child);
        if (createChild.IsError || createChild.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = createChild.Message };

        // Copy-by-reference: for nodes with ExecutionOrder < forkPoint, the
        // parent's execution row is shared with the child. The InMemory
        // implementation does this by creating a new (childRunId, nodeId)
        // row that mirrors the parent's execution state (same Id, same
        // Output/Error/State). The SurrealDB write-through (surrealdb-migration
        // tasks 9–10) materializes this as a `RELATE quest_run -> executes ->
        // quest_node_execution` edge — no duplication. See SURREAL-SCHEMA-HINTS §6.2.
        var parentExecs = (await _executionStore.GetByRunIdAsync(parent.Id)).Result
            ?? Enumerable.Empty<QuestNodeExecution>();
        foreach (var parentExec in parentExecs)
        {
            var parentNode = quest.Nodes.FirstOrDefault(n => n.Id == parentExec.NodeId);
            if (parentNode == null) continue;
            if (parentNode.ExecutionOrder >= forkPoint) continue;

            // Mirror the parent's completed execution onto the child run.
            var mirror = new QuestNodeExecution
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
            if (pe.State != QuestNodeState.Pending && pe.State != QuestNodeState.Running) continue;
            var observedState = pe.State;
            pe.State = QuestNodeState.Cancelled;
            pe.EndedAt = DateTime.UtcNow;
            await _executionStore.UpdateAsync(pe, expectedState: observedState);
        }

        // Transition parent Running → Forked (terminal).
        parent.Status = QuestRunStatus.Forked;
        parent.EndedAt = DateTime.UtcNow;
        await _runStore.UpdateAsync(parent);

        return new AZOAResult<QuestRun> { Result = child, Message = "Fork created." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // SUPERVISOR-DRIVEN FAIL
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<QuestRun>> MarkRunFailedAsync(Guid runId, string reason, Guid avatarId, AZOARequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, avatarId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = runResult.Message };

        var run = runResult.Result;
        if (run.Status != QuestRunStatus.Running)
        {
            return new AZOAResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot mark run {runId} failed: status is {run.Status} (only Running runs accept supervisor fail)."
            };
        }

        // Cancel any in-flight node executions (same shape as fork).
        // HIGH#7 — same state-machine guard as ForkAsync (line ~509).
        var execs = (await _executionStore.GetByRunIdAsync(run.Id)).Result
            ?? Enumerable.Empty<QuestNodeExecution>();
        foreach (var exec in execs)
        {
            if (exec.State != QuestNodeState.Pending && exec.State != QuestNodeState.Running) continue;
            var observedState = exec.State;
            exec.State = QuestNodeState.Cancelled;
            exec.EndedAt = DateTime.UtcNow;
            await _executionStore.UpdateAsync(exec, expectedState: observedState);
        }

        run.Status = QuestRunStatus.Failed;
        run.FailReason = reason;
        run.EndedAt = DateTime.UtcNow;
        var updated = await _runStore.UpdateAsync(run);

        return new AZOAResult<QuestRun> { Result = updated.Result, Message = $"Run marked failed: {reason}" };
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<QuestTemplate>> CreateTemplateAsync(QuestTemplateCreateModel model, Guid avatarId, AZOARequest? request = null)
    {
        var template = new QuestTemplate
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            Description = model.Description,
            AuthorAvatarId = avatarId,
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

            template.Nodes.Add(new QuestTemplateNode
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
                return new AZOAResult<QuestTemplate> { IsError = true, Message = "Edge index out of range." };
            }

            template.Edges.Add(new QuestTemplateEdge
            {
                Id = Guid.NewGuid(),
                TemplateId = template.Id,
                SourceSlotId = slotIds[edgeModel.SourceNodeId],
                TargetSlotId = slotIds[edgeModel.TargetNodeId],
                EdgeType = edgeModel.EdgeType
            });
        }

        return await _questStore.UpsertQuestTemplateAsync(template);
    }

    public async Task<AZOAResult<QuestTemplate>> GetTemplateAsync(Guid id, AZOARequest? request = null)
    {
        return await _questStore.GetQuestTemplateAsync(id);
    }

    public async Task<AZOAResult<IEnumerable<QuestTemplate>>> ListTemplatesAsync(AZOARequest? request = null)
    {
        return await _questStore.GetAllQuestTemplatesAsync();
    }

    public async Task<AZOAResult<Quest>> InstantiateTemplateAsync(Guid templateId, Guid avatarId, Dictionary<string, string>? parameters = null, AZOARequest? request = null)
    {
        var templateResult = await _questStore.GetQuestTemplateAsync(templateId);
        if (templateResult.IsError || templateResult.Result == null)
            return new AZOAResult<Quest> { IsError = true, Message = templateResult.Message };

        var template = templateResult.Result;

        // Load node templates referenced by this quest template
        var nodeTemplatesResult = await _questStore.GetAllQuestNodeTemplatesAsync();
        var nodeTemplates = (nodeTemplatesResult.Result ?? Enumerable.Empty<QuestNodeTemplate>())
            .ToDictionary(nt => nt.Id);

        var quest = new Quest
        {
            Id = Guid.NewGuid(),
            Name = template.Name,
            Description = template.Description,
            AvatarId = avatarId,
            TemplateId = templateId,
            // Denormalise origin onto the copy: the template's author is the creator
            // this instantiated quest descends from (marketplace provenance).
            OriginAvatarId = template.AuthorAvatarId,
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

            var nodeType = nodeTemplate?.NodeType ?? QuestNodeType.HolonGet;

            quest.Nodes.Add(new QuestNode
            {
                Id = nodeId,
                QuestId = quest.Id,
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

            quest.Edges.Add(new QuestEdge
            {
                Id = Guid.NewGuid(),
                QuestId = quest.Id,
                SourceNodeId = sourceId,
                TargetNodeId = targetId,
                EdgeType = templateEdge.EdgeType
            });
        }

        return await _questStore.UpsertQuestAsync(quest);
    }

    // ═══════════════════════════════════════════════════════════════════
    // NODE TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<QuestNodeTemplate>> CreateNodeTemplateAsync(QuestNodeTemplateCreateModel model, Guid avatarId, AZOARequest? request = null)
    {
        var template = new QuestNodeTemplate
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
            AuthorAvatarId = avatarId,
            IsPublic = model.IsPublic,
            Tags = model.Tags
        };

        return await _questStore.UpsertQuestNodeTemplateAsync(template);
    }

    public async Task<AZOAResult<IEnumerable<QuestNodeTemplate>>> ListNodeTemplatesAsync(AZOARequest? request = null)
    {
        return await _questStore.GetAllQuestNodeTemplatesAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    // QUEST NODES SUB-RESOURCE (post-hoc edits on a persisted Quest)
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<IEnumerable<QuestNode>>> ListNodesAsync(Guid questId, Guid avatarId, AZOARequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<IEnumerable<QuestNode>> { IsError = true, Message = questResult.Message };

        return new AZOAResult<IEnumerable<QuestNode>>
        {
            Result = questResult.Result.Nodes,
            Message = "Success"
        };
    }

    public async Task<AZOAResult<QuestNode>> AddNodeAsync(Guid questId, QuestNodeCreateModel model, Guid avatarId, AZOARequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<QuestNode> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        // FR-2 / AC-2c: mutations are blocked while the quest is Active.
        if (quest.Status == QuestStatus.Active)
            return new AZOAResult<QuestNode> { IsError = true, Message = "Cannot mutate an Active quest. Call POST /{id}/unpublish first." };

        // FR-4 / AC-4b: validate config schema before persisting.
        var configValidationError = QuestNodeConfigRegistry.Validate(model.NodeType, model.Config);
        if (configValidationError != null)
            return new AZOAResult<QuestNode> { IsError = true, Message = $"Node config error: {configValidationError}" };

        var node = new QuestNode
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            Name = model.Name,
            NodeType = model.NodeType,
            Config = model.Config,
            IsEntry = model.IsEntry,
            IsTerminal = model.IsTerminal,
            NodeTemplateId = model.NodeTemplateId
        };
        quest.Nodes.Add(node);

        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new AZOAResult<QuestNode> { IsError = true, Message = upsert.Message };

        return new AZOAResult<QuestNode> { Result = node, Message = "Node added." };
    }

    public async Task<AZOAResult<QuestNode>> UpdateNodeAsync(Guid questId, Guid nodeId, QuestNodeUpdateModel model, Guid avatarId, AZOARequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<QuestNode> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        // FR-2 / AC-2c: mutations are blocked while the quest is Active.
        if (quest.Status == QuestStatus.Active)
            return new AZOAResult<QuestNode> { IsError = true, Message = "Cannot mutate an Active quest. Call POST /{id}/unpublish first." };
        var node = quest.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return new AZOAResult<QuestNode> { IsError = true, Message = $"Node {nodeId} not found in quest {questId}." };

        // FR-4 / AC-4b: validate new config schema before applying.
        if (model.Config != null)
        {
            var configValidationError = QuestNodeConfigRegistry.Validate(node.NodeType, model.Config);
            if (configValidationError != null)
                return new AZOAResult<QuestNode> { IsError = true, Message = $"Node config error: {configValidationError}" };
        }

        // Patch semantics: only non-null fields are applied.
        if (model.Name != null) node.Name = model.Name;
        if (model.Config != null) node.Config = model.Config;
        if (model.IsEntry.HasValue) node.IsEntry = model.IsEntry.Value;
        if (model.IsTerminal.HasValue) node.IsTerminal = model.IsTerminal.Value;

        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new AZOAResult<QuestNode> { IsError = true, Message = upsert.Message };

        return new AZOAResult<QuestNode> { Result = node, Message = "Node updated." };
    }

    public async Task<AZOAResult<bool>> DeleteNodeAsync(Guid questId, Guid nodeId, Guid avatarId, AZOARequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        // FR-2 / AC-2c: mutations are blocked while the quest is Active.
        if (quest.Status == QuestStatus.Active)
            return new AZOAResult<bool> { IsError = true, Message = "Cannot mutate an Active quest. Call POST /{id}/unpublish first." };
        var node = quest.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return new AZOAResult<bool> { IsError = true, Message = $"Node {nodeId} not found in quest {questId}." };

        // Reject if node has any edges referencing it — orphaning edges would
        // produce an invalid DAG. Callers must clear edges first via
        // RemoveEdgeAsync. This preserves the graph invariant that every edge
        // endpoint references an existing node.
        var referencingEdges = quest.Edges
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

        quest.Nodes.Remove(node);
        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new AZOAResult<bool> { IsError = true, Message = upsert.Message };

        return new AZOAResult<bool> { Result = true, Message = "Node deleted." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // QUEST EDGES SUB-RESOURCE
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<QuestEdge>> AddEdgeAsync(Guid questId, QuestEdgeAddModel model, Guid avatarId, AZOARequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<QuestEdge> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        // FR-2 / AC-2c: mutations are blocked while the quest is Active.
        if (quest.Status == QuestStatus.Active)
            return new AZOAResult<QuestEdge> { IsError = true, Message = "Cannot mutate an Active quest. Call POST /{id}/unpublish first." };

        // Endpoint existence guard — both ends must reference nodes already in
        // the quest. Without this an invalid graph can be persisted and only
        // discovered at validate/execute time.
        if (quest.Nodes.All(n => n.Id != model.SourceNodeId))
            return new AZOAResult<QuestEdge> { IsError = true, Message = $"SourceNodeId {model.SourceNodeId} is not present in quest {questId}." };
        if (quest.Nodes.All(n => n.Id != model.TargetNodeId))
            return new AZOAResult<QuestEdge> { IsError = true, Message = $"TargetNodeId {model.TargetNodeId} is not present in quest {questId}." };
        if (model.SourceNodeId == model.TargetNodeId)
            return new AZOAResult<QuestEdge> { IsError = true, Message = "SourceNodeId and TargetNodeId must not be the same node (self-loops not allowed)." };

        var edge = new QuestEdge
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            SourceNodeId = model.SourceNodeId,
            TargetNodeId = model.TargetNodeId,
            Condition = model.Condition,
            EdgeType = model.EdgeType
        };
        quest.Edges.Add(edge);

        // Cycle guard: run the DAG validator after staging the new edge and
        // reject only on cycle-class errors. We deliberately ignore the
        // entry/terminal/orphan checks because a Quest is editable in-flight
        // — terminal/entry flags and full reachability are only required
        // immediately before ExecuteAsync (which runs the full ValidateDAGAsync).
        // Allowing orphan-class errors here would prevent any incremental
        // edge wiring on a partially-built graph.
        var validation = _dagValidator.Validate(quest);
        var cycleErrors = validation.Errors
            .Where(e => e.Contains("Cycle detected", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (cycleErrors.Count > 0)
        {
            quest.Edges.Remove(edge);
            return new AZOAResult<QuestEdge>
            {
                IsError = true,
                Message = $"Edge would invalidate DAG: {string.Join("; ", cycleErrors)}"
            };
        }

        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new AZOAResult<QuestEdge> { IsError = true, Message = upsert.Message };

        return new AZOAResult<QuestEdge> { Result = edge, Message = "Edge added." };
    }

    public async Task<AZOAResult<bool>> RemoveEdgeAsync(Guid questId, Guid edgeId, Guid avatarId, AZOARequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        // FR-2 / AC-2c: mutations are blocked while the quest is Active.
        if (quest.Status == QuestStatus.Active)
            return new AZOAResult<bool> { IsError = true, Message = "Cannot mutate an Active quest. Call POST /{id}/unpublish first." };
        var edge = quest.Edges.FirstOrDefault(e => e.Id == edgeId);
        if (edge == null)
            return new AZOAResult<bool> { IsError = true, Message = $"Edge {edgeId} not found in quest {questId}." };

        quest.Edges.Remove(edge);
        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new AZOAResult<bool> { IsError = true, Message = upsert.Message };

        return new AZOAResult<bool> { Result = true, Message = "Edge removed." };
    }

    public async Task<AZOAResult<IEnumerable<Guid>>> GetTopologicalOrderAsync(Guid questId, Guid avatarId, AZOARequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<IEnumerable<Guid>> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;

        // QuestDagValidator is the single ExecutionOrder authority — Validate
        // mutates node.ExecutionOrder in-place. Persist the order so subsequent
        // reads of the quest definition see the validator-assigned positions.
        var validation = _dagValidator.Validate(quest);
        if (!validation.IsValid)
        {
            return new AZOAResult<IEnumerable<Guid>>
            {
                IsError = true,
                Message = $"DAG validation failed: {string.Join("; ", validation.Errors)}"
            };
        }

        await _questStore.UpsertQuestAsync(quest);

        var ordered = quest.Nodes
            .OrderBy(n => n.ExecutionOrder)
            .Select(n => n.Id)
            .ToList();

        return new AZOAResult<IEnumerable<Guid>> { Result = ordered, Message = "Success" };
    }

    // ═══════════════════════════════════════════════════════════════════
    // QUEST DEPENDENCIES SUB-RESOURCE
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<QuestDependency>> AddDependencyAsync(Guid questId, QuestDependencyCreateModel model, Guid avatarId, AZOARequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<QuestDependency> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;

        if (model.DependsOnQuestId == Guid.Empty)
            return new AZOAResult<QuestDependency> { IsError = true, Message = "DependsOnQuestId must not be an empty GUID." };
        if (model.DependsOnQuestId == questId)
            return new AZOAResult<QuestDependency> { IsError = true, Message = "A quest may not depend on itself." };

        var dependency = new QuestDependency
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            DependsOnQuestId = model.DependsOnQuestId,
            DependsOnNodeId = model.DependsOnNodeId,
            DependencyType = model.DependencyType
        };
        quest.Dependencies.Add(dependency);

        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new AZOAResult<QuestDependency> { IsError = true, Message = upsert.Message };

        return new AZOAResult<QuestDependency> { Result = dependency, Message = "Dependency added." };
    }

    public async Task<AZOAResult<bool>> RemoveDependencyAsync(Guid questId, Guid depId, Guid avatarId, AZOARequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        var dep = quest.Dependencies.FirstOrDefault(d => d.Id == depId);
        if (dep == null)
            return new AZOAResult<bool> { IsError = true, Message = $"Dependency {depId} not found in quest {questId}." };

        quest.Dependencies.Remove(dep);
        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new AZOAResult<bool> { IsError = true, Message = upsert.Message };

        return new AZOAResult<bool> { Result = true, Message = "Dependency removed." };
    }

    public async Task<AZOAResult<DependencyCheckResult>> CheckDependenciesAsync(Guid questId, Guid avatarId, AZOARequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<DependencyCheckResult> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        var unsatisfied = new List<Guid>();

        foreach (var dep in quest.Dependencies)
        {
            var runs = await _runStore.GetByQuestIdAsync(dep.DependsOnQuestId);
            var anySucceeded = runs.Result?.Any(r => r.Status == QuestRunStatus.Succeeded) ?? false;
            if (!anySucceeded)
                unsatisfied.Add(dep.Id);
        }

        var check = new DependencyCheckResult
        {
            AllSatisfied = unsatisfied.Count == 0,
            UnsatisfiedDependencyIds = unsatisfied,
            Message = unsatisfied.Count == 0
                ? "All dependencies satisfied."
                : $"{unsatisfied.Count} dependency(ies) not yet satisfied."
        };

        return new AZOAResult<DependencyCheckResult> { Result = check, Message = "Success" };
    }

    /// <summary>
    /// final-hardening F4: ENFORCE quest dependencies at run start (fail-closed). Both
    /// run-start paths (<see cref="ExecuteAsync"/> legacy + <see cref="StartWorkflowRunAsync"/>
    /// durable) call this after DAG validation and before any run/exec rows are written —
    /// so an unsatisfied dependency rejects the run cleanly with NO orphaned run created.
    /// Reuses <see cref="CheckDependenciesAsync"/> as the single source of truth for what
    /// "satisfied" means (any Succeeded run of each depended-on quest), so the manual
    /// check endpoint and the enforced gate can never diverge. Returns an error result
    /// when a dependency is unsatisfied (or the check itself faults — fail closed);
    /// a null return means the run may proceed. See <c>Managers/AGENTS.md</c>
    /// §quest-dependency-enforcement.
    /// </summary>
    private async Task<AZOAResult<QuestRun>?> EnforceDependenciesAsync(Guid questId, Guid avatarId, AZOARequest? request)
    {
        var depCheck = await CheckDependenciesAsync(questId, avatarId, request);
        if (depCheck.IsError || depCheck.Result == null)
        {
            // A faulted dependency check fails the run closed — we cannot prove the
            // dependencies are satisfied, so we must not start the run.
            return new AZOAResult<QuestRun>
            {
                IsError = true,
                Message = $"Dependency check failed; run rejected (fail-closed): {depCheck.Message}"
            };
        }

        if (!depCheck.Result.AllSatisfied)
        {
            return new AZOAResult<QuestRun>
            {
                IsError = true,
                Message = $"Quest {questId} cannot start: {depCheck.Result.Message} " +
                          $"Unsatisfied dependency ids: [{string.Join(", ", depCheck.Result.UnsatisfiedDependencyIds)}]. " +
                          "Each depended-on quest must have at least one Succeeded run first."
            };
        }

        return null; // all satisfied — proceed
    }

    // ═══════════════════════════════════════════════════════════════════
    // QUESTRUN READ SURFACE
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<QuestRun>> GetRunAsync(Guid runId, Guid avatarId, AZOARequest? request = null)
    {
        return await LoadOwnedRunAsync(runId, avatarId);
    }

    /// <summary>
    /// Pre-run disclosure: the value-moving-node manifest a caller sees BEFORE
    /// committing a marketplace run, so they can consent knowingly. Scoped exactly
    /// like a run-start (owner may preview their own; a non-owner may preview a
    /// published quest). ExecutionOrder is (re)assigned so the manifest lists nodes
    /// in run order. See Managers/AGENTS.md §economic-consent.
    /// </summary>
    public async Task<AZOAResult<QuestEconomicManifest>> PreviewRunAsync(Guid questId, Guid avatarId, AZOARequest? request = null)
    {
        var (startable, _) = await LoadStartableQuestAsync(questId, avatarId);
        if (startable.IsError || startable.Result == null)
            return new AZOAResult<QuestEconomicManifest> { IsError = true, Message = startable.Message };

        var quest = startable.Result;
        // Assign ExecutionOrder for run-order manifest listing (best-effort — an
        // invalid DAG still yields a manifest, just unordered).
        _dagValidator.Validate(quest);

        var manifest = QuestEconomicManifestBuilder.Build(quest, _registry, quest.PublishedVersionHash);
        return new AZOAResult<QuestEconomicManifest> { Result = manifest, Message = "Run preview computed." };
    }

    public async Task<AZOAResult<IEnumerable<QuestRun>>> ListRunsByQuestAsync(Guid questId, Guid avatarId, AZOARequest? request = null)
    {
        var owned = await LoadOwnedQuestAsync(questId, avatarId);
        if (owned.IsError || owned.Result == null)
            return new AZOAResult<IEnumerable<QuestRun>> { IsError = true, Message = owned.Message };

        return await _runStore.GetByQuestIdAsync(questId);
    }

    public async Task<AZOAResult<QuestExecutionState>> GetExecutionStateAsync(Guid runId, Guid avatarId, AZOARequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, avatarId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<QuestExecutionState> { IsError = true, Message = runResult.Message };

        var run = runResult.Result;
        var execsResult = await _executionStore.GetByRunIdAsync(runId);
        var execs = (execsResult.Result ?? Enumerable.Empty<QuestNodeExecution>()).ToList();

        // Counts are derived from rows on every read — no risk of drift.
        // "Pending" here groups Pending + Running as the not-yet-terminal bucket
        // because the API consumer cares about "how many are still in flight".
        var state = new QuestExecutionState
        {
            RunId = run.Id,
            QuestId = run.QuestId,
            Status = run.Status,
            StartedAt = run.StartedAt,
            EndedAt = run.EndedAt,
            TotalNodes = execs.Count,
            CompletedNodes = execs.Count(e => e.State == QuestNodeState.Succeeded),
            FailedNodes = execs.Count(e => e.State == QuestNodeState.Failed),
            PendingNodes = execs.Count(e => e.State == QuestNodeState.Pending || e.State == QuestNodeState.Running),
            NodeExecutions = execs
        };

        return new AZOAResult<QuestExecutionState> { Result = state, Message = "Success" };
    }

    public async Task<AZOAResult<QuestRun>> MarkRunCompletedAsync(Guid runId, Guid avatarId, AZOARequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, avatarId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = runResult.Message };

        var run = runResult.Result;

        // State-machine guard, mirrored from MarkRunFailedAsync: only Running
        // runs may be transitioned to Succeeded/Failed by this supervisor path.
        if (run.Status != QuestRunStatus.Running)
        {
            return new AZOAResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot mark run {runId} completed: status is {run.Status} (only Running runs accept supervisor complete)."
            };
        }

        // In-flight guard: every QuestNodeExecution must be terminal before the
        // run can be marked completed. Pending / Running rows indicate the run
        // still has work outstanding — refusing this transition prevents an
        // orchestrator from prematurely closing a run that has live work.
        var execsResult = await _executionStore.GetByRunIdAsync(runId);
        var execs = (execsResult.Result ?? Enumerable.Empty<QuestNodeExecution>()).ToList();
        var inFlight = execs
            .Where(e => e.State == QuestNodeState.Pending || e.State == QuestNodeState.Running)
            .ToList();
        if (inFlight.Count > 0)
        {
            return new AZOAResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot mark run {runId} completed: {inFlight.Count} node execution(s) still in flight (Pending/Running)."
            };
        }

        // Terminal status is Failed if any node execution failed, otherwise
        // Succeeded. Mirrors the same derivation used at the end of
        // ExecuteAsync so the supervisor-driven completion produces the same
        // overall verdict as the in-process loop would have.
        run.Status = execs.Any(e => e.State == QuestNodeState.Failed)
            ? QuestRunStatus.Failed
            : QuestRunStatus.Succeeded;
        run.EndedAt = DateTime.UtcNow;
        var updated = await _runStore.UpdateAsync(run);

        return new AZOAResult<QuestRun> { Result = updated.Result, Message = $"Run marked {run.Status}." };
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
    public async Task<AZOAResult<QuestRun>> StartWorkflowRunAsync(Guid questId, Guid avatarId, AZOARequest? request = null, Guid? actingTenantId = null, bool acknowledgeEconomicEffects = false)
    {
        // Marketplace mechanic (durable path): owner may start their own quest; a
        // non-owner may start it only when published (public + Active). Provenance is
        // stamped on the run below, mirroring the legacy ExecuteAsync path.
        var (startable, isOwner) = await LoadStartableQuestAsync(questId, avatarId);
        if (startable.IsError || startable.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = startable.Message };

        // Marketplace guard 1 — per-(avatar,quest) run-start quota (treasury/runner drain).
        var quotaGate = await EnforceRunQuotaAsync(questId, avatarId, isOwner);
        if (quotaGate is not null)
            return quotaGate;

        // FR-2: require Active status before starting a durable workflow run.
        if (startable.Result.Status != QuestStatus.Active)
            return new AZOAResult<QuestRun> { IsError = true, Message = "Quest must be published (Active) before a workflow run can be started. Call POST /{id}/publish first." };

        var validationResult = await ValidateDAGAsync(questId, request);
        if (validationResult.IsError)
            return new AZOAResult<QuestRun> { IsError = true, Message = validationResult.Message };

        // FR-3 / AC-3a: fan-out is an error on the durable path — reject before any
        // saga rows are written (ResolveSingleSuccessor catches it at runtime too,
        // but catching here gives a cleaner, pre-run error message).
        var questForFanOut = (await _questStore.GetQuestAsync(questId)).Result;
        if (questForFanOut != null)
        {
            var fanOutCheck = _dagValidator.Validate(questForFanOut, fanOutAsError: true);
            var fanOutErrors = fanOutCheck.Errors.Where(e => e.Contains("fan-out", StringComparison.OrdinalIgnoreCase)).ToList();
            if (fanOutErrors.Count > 0)
                return new AZOAResult<QuestRun> { IsError = true, Message = $"Workflow run rejected — fan-out not supported on durable path: {string.Join("; ", fanOutErrors)}" };
        }

        // final-hardening F4: enforce quest dependencies at run start (fail-closed).
        // Rejected BEFORE the run + per-node executions are written and before the entry
        // node is enqueued as a saga step — no orphaned durable run on rejection.
        var depGate = await EnforceDependenciesAsync(questId, avatarId, request);
        if (depGate is not null)
            return depGate;

        var questResult = await _questStore.GetQuestAsync(questId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = questResult.Message };
        var quest = questResult.Result;

        // Marketplace guard 2 — economic-consent gate (durable path). Mirrors
        // ExecuteAsync: a non-owner run containing value-moving nodes is rejected
        // unless the runner acknowledged the disclosed effects. ValidateDAGAsync above
        // assigned ExecutionOrder so the manifest lists nodes in run order.
        var consentGate = EnforceEconomicConsent(quest, isOwner, acknowledgeEconomicEffects);
        if (consentGate is not null)
            return consentGate;

        var entry = ResolveEntryNode(quest);
        if (entry is null)
            return new AZOAResult<QuestRun> { IsError = true, Message = "Quest has no entry node to start the workflow run." };

        // F6 TOCTOU guard: confirm the definition is STILL Active at the version we
        // read before committing a durable run -- same discipline as ExecuteAsync.
        // Closes unpublish racing a workflow-run-start on the durable path.
        var confirmed = await _questStore.TryConfirmQuestStateAsync(
            quest.Id, QuestStatus.Active, quest.Version);
        if (confirmed == 0)
            return new AZOAResult<QuestRun>
            {
                IsError = true,
                Message = "Quest run conflict: the quest was unpublished or modified concurrently. Reload and retry."
            };

        // Create the run + one Pending QuestNodeExecution per node — identical to
        // ExecuteAsync so the per-node claim/idempotency surface is shared.
        var run = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            // Stamp the run to the RUNNER (marketplace mechanic) — owner path: equal;
            // non-owner path: runner's own context with provenance back to origin.
            AvatarId = avatarId,
            // tenant-consent-delegation AC4: persist the acting tenant on the
            // durable run so it survives the async saga-worker hop and reaches the
            // Tier-2 node handlers via the QuestNodeExecutionContext. This is the
            // keystone — the durable path is where the acting tenant would otherwise
            // be lost (no ambient principal on the saga worker).
            ActingTenantId = actingTenantId,
            // Marketplace provenance: null on owner path, origin quest + creator on
            // the non-owner path.
            SourceQuestId = isOwner ? null : quest.Id,
            OriginAvatarId = isOwner ? null : quest.AvatarId,
            // Bind the run to the exact published graph revision (bait-and-switch guard).
            PublishedVersionHash = quest.PublishedVersionHash,
            Status = QuestRunStatus.Pending,
            StartedAt = DateTime.UtcNow
        };
        var createRun = await _runStore.CreateAsync(run);
        if (createRun.IsError || createRun.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = createRun.Message };

        foreach (var node in quest.Nodes)
        {
            var exec = new QuestNodeExecution
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                NodeId = node.Id,
                State = QuestNodeState.Pending,
                StartedAt = DateTime.UtcNow
            };
            var createExec = await _executionStore.CreateAsync(exec);
            if (createExec.IsError)
                return new AZOAResult<QuestRun> { IsError = true, Message = createExec.Message };
        }

        // Enqueue the entry node as the first saga step (step name = node id).
        // Started directly via the store, not the coordinator, because the
        // quest-workflow definition is not a SagaDefinition (D1, Approach A).
        await EnqueueWorkflowNodeAsync(run.Id, quest.Id, run.AvatarId, entry.Id, signalPayload: null);

        run.Status = QuestRunStatus.Running;
        var updated = await _runStore.UpdateAsync(run);
        return new AZOAResult<QuestRun> { Result = updated.Result ?? run, Message = "Workflow run started." };
    }

    /// <summary>
    /// The <c>step(nodeId)</c> primitive: resume a SUSPENDED manual-advance run
    /// from <paramref name="fromNodeId"/> into its single Control successor by
    /// enqueuing that downstream node as a fresh saga step. Avatar-scoped; only
    /// Suspended runs accept advance (mirrors the <see cref="MarkRunCompletedAsync"/>
    /// state-machine guard).
    /// </summary>
    public async Task<AZOAResult<QuestRun>> AdvanceAsync(Guid runId, Guid fromNodeId, Guid avatarId, AZOARequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, avatarId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = runResult.Message };
        var run = runResult.Result;

        if (run.Status is not (QuestRunStatus.Suspended or QuestRunStatus.AwaitingSignal))
            return new AZOAResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot advance run {runId}: status is {run.Status} (only Suspended/AwaitingSignal runs accept advance)."
            };

        var questResult = await _questStore.GetQuestAsync(run.QuestId);
        if (questResult.IsError || questResult.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = questResult.Message };
        var quest = questResult.Result;

        // Resolve the next hop through the SAME authority the engine's
        // auto-advance uses, so the manual and auto paths can never route the
        // same graph to different successors (or disagree on the fan-out guard).
        var hop = QuestWorkflowEdges.ResolveSingleSuccessor(quest, fromNodeId);
        switch (hop.Kind)
        {
            case SuccessorKind.Terminal:
                // Manual-advance from a terminal node ⇒ the run completes.
                run.Status = QuestRunStatus.Succeeded;
                run.EndedAt = DateTime.UtcNow;
                var done = await _runStore.UpdateAsync(run);
                return new AZOAResult<QuestRun> { Result = done.Result ?? run, Message = "Workflow run completed (advanced past terminal node)." };

            case SuccessorKind.FanOut:
                return new AZOAResult<QuestRun>
                {
                    IsError = true,
                    Message = $"Cannot advance: node {fromNodeId} has {hop.Count} Control successors (fan-out is out of scope)."
                };

            default: // Single
                await EnqueueWorkflowNodeAsync(run.Id, run.QuestId, run.AvatarId, hop.NodeId!.Value, signalPayload: null);
                run.Status = QuestRunStatus.Running;
                var updated = await _runStore.UpdateAsync(run);
                return new AZOAResult<QuestRun> { Result = updated.Result ?? run, Message = "Workflow run advanced." };
        }
    }

    /// <summary>
    /// Deliver an external signal to a PARKED gate node: un-park the matching
    /// saga step (G2 single-winner via <c>ISagaStore.TrySignalAsync</c>) so the
    /// processor resumes the gate node, carrying <paramref name="payload"/> into
    /// it. Avatar-scoped; idempotent — a duplicate signal un-parks at most once.
    /// </summary>
    public async Task<AZOAResult<QuestRun>> SignalAsync(Guid runId, string gateId, string? payload, Guid avatarId, AZOARequest? request = null)
    {
        if (string.IsNullOrWhiteSpace(gateId))
            return new AZOAResult<QuestRun> { IsError = true, Message = "Signal requires a non-empty gateId." };

        var runResult = await LoadOwnedRunAsync(runId, avatarId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<QuestRun> { IsError = true, Message = runResult.Message };
        var run = runResult.Result;

        if (run.Status is not (QuestRunStatus.AwaitingSignal or QuestRunStatus.AwaitingTimer or QuestRunStatus.Suspended))
            return new AZOAResult<QuestRun>
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
            return new AZOAResult<QuestRun>
            {
                IsError = true,
                Message = $"No node in run {runId} is parked on gate '{gateId}' (already signalled, or wrong gate)."
            };

        run.Status = QuestRunStatus.Running;
        var updated = await _runStore.UpdateAsync(run);
        return new AZOAResult<QuestRun> { Result = updated.Result ?? run, Message = "Signal delivered; workflow run resuming." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // RECONCILE-BEFORE-RETRY RE-PROBE (P7, blockchain-recovery-and-portable-wallets §1.4)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Manual re-probe of a single run parked in
    /// <see cref="QuestRunStatus.AwaitingReconciliation"/>. Avatar-scoped; rejects a
    /// run not in that state. Delegates to <see cref="ReprobeReconciliationAsync"/>,
    /// the shared chain-truth logic that NEVER re-broadcasts.
    /// </summary>
    public async Task<AZOAResult<QuestReconciliationResult>> ReconcileRunAsync(Guid runId, Guid avatarId, AZOARequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, avatarId);
        if (runResult.IsError || runResult.Result == null)
            return new AZOAResult<QuestReconciliationResult> { IsError = true, Message = runResult.Message };

        var run = runResult.Result;
        if (run.Status != QuestRunStatus.AwaitingReconciliation)
            return new AZOAResult<QuestReconciliationResult>
            {
                IsError = true,
                Message = $"Cannot reconcile run {runId}: status is {run.Status} (only AwaitingReconciliation runs accept a re-probe)."
            };

        var result = await ReprobeReconciliationAsync(run);
        return new AZOAResult<QuestReconciliationResult> { Result = result, Message = "Reconciliation re-probe complete." };
    }

    /// <summary>
    /// Operator/background sweep: re-probe EVERY run parked in
    /// <see cref="QuestRunStatus.AwaitingReconciliation"/>. Unscoped (operator
    /// context). Each run runs through the same <see cref="ReprobeReconciliationAsync"/>
    /// chain-truth logic; a probe failure on one run does not abort the sweep.
    /// </summary>
    public async Task<AZOAResult<IEnumerable<QuestReconciliationResult>>> SweepReconciliationAsync(AZOARequest? request = null)
    {
        var parked = await _runStore.GetByStatusAsync(QuestRunStatus.AwaitingReconciliation);
        if (parked.IsError)
            return new AZOAResult<IEnumerable<QuestReconciliationResult>> { IsError = true, Message = parked.Message };

        var results = new List<QuestReconciliationResult>();
        foreach (var run in parked.Result ?? Enumerable.Empty<QuestRun>())
            results.Add(await ReprobeReconciliationAsync(run));

        return new AZOAResult<IEnumerable<QuestReconciliationResult>>
        {
            Result = results,
            Message = $"Swept {results.Count} run(s) awaiting reconciliation."
        };
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
    /// <para>The run is left in <see cref="QuestRunStatus.AwaitingReconciliation"/>
    /// while ANY node remains indeterminate; once a node is un-parked the saga
    /// processor projects the run back to Running as it resumes. We never force a
    /// terminal status here — the engine (success advance) or the saga
    /// (retry/compensation) owns the terminal verdict, exactly as on the live path.</para>
    /// </summary>
    private async Task<QuestReconciliationResult> ReprobeReconciliationAsync(QuestRun run)
    {
        var outcome = new QuestReconciliationResult { RunId = run.Id, Status = run.Status };

        var execsResult = await _executionStore.GetByRunIdAsync(run.Id);
        var execs = (execsResult.Result ?? Enumerable.Empty<QuestNodeExecution>()).ToList();

        // Only Failed rows carrying a broadcast hash are candidates: those are the
        // parked chain-action nodes the step handler stamped before parking. A
        // Failed row with no hash was a non-broadcast failure (parked because we
        // could not prove anything was on the wire) — re-probing has nothing to
        // probe, so it stays parked for an operator.
        var candidates = execs
            .Where(e => e.State == QuestNodeState.Failed && !string.IsNullOrWhiteSpace(e.TxHash))
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
                    exec.State = QuestNodeState.Succeeded;
                    exec.Error = null;
                    exec.EndedAt = DateTime.UtcNow;
                    var reconciled = await _executionStore.UpdateAsync(
                        exec, expectedState: QuestNodeState.Failed);
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
                : _chainFactory.GetProvider(chainType, ChainNetwork.Devnet);
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
    private static QuestNode? ResolveEntryNode(Quest quest)
    {
        var entry = quest.Nodes.FirstOrDefault(n => n.IsEntry);
        if (entry is not null)
            return entry;
        return quest.Nodes.OrderBy(n => n.ExecutionOrder).FirstOrDefault();
    }

    /// <summary>Enqueue a quest node as a saga step (the first step of a run, or
    /// a manual/signal-driven resume). Mirrors the handler's self-advance enqueue
    /// so the correlation key, step name, and idempotency key are derived
    /// identically.</summary>
    private async Task EnqueueWorkflowNodeAsync(
        Guid runId, Guid questId, Guid avatarId, Guid nodeId, string? signalPayload)
    {
        var payload = new QuestStepPayload(runId, questId, avatarId, nodeId, signalPayload);
        var stepName = nodeId.ToString();
        var idemKey = SagaKeys.StepIdempotencyKey(runId.ToString(), stepName);
        await _sagaStore.EnqueueNextStepAsync(
            QuestWorkflowSaga.Name, stepName, runId.ToString(),
            idemKey, SagaStep<QuestStepPayload>.Serialize(payload), CancellationToken.None);
    }

    /// <summary>Re-serialize the parked gate step's payload with the delivered
    /// signal body so the resumed node sees <c>SignalPayload != null</c> and
    /// falls through to do its work instead of re-parking. Pure: the result is
    /// handed to <c>TrySignalAsync</c> to write atomically with the un-park.
    /// Returns <c>null</c> when the existing payload is unparseable (leave it
    /// as-is; the node re-parks — safe, not lost).</summary>
    private static string? BuildSignalStampedPayload(string existingPayloadJson, string? signalPayload)
    {
        QuestStepPayload? parsed;
        try
        {
            parsed = System.Text.Json.JsonSerializer.Deserialize<QuestStepPayload>(existingPayloadJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (parsed is null)
            return null;

        var stamped = parsed with { SignalPayload = signalPayload ?? string.Empty };
        return SagaStep<QuestStepPayload>.Serialize(stamped);
    }
}
