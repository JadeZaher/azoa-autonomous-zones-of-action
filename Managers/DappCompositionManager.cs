using System.Text.Json;
using AZOA.WebAPI.Core.Json;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
// Disambiguate `Quest`: the source-gen'd POCO (Generated.SurrealDb.Quest) is
// not yet wired through IQuestStore -- the hand-written model stays in
// service until the quest cutover slice lands. Alias keeps the manager
// readable without scattering fully-qualified type names everywhere.
using QuestDef = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Managers;

public sealed class DappCompositionManager : IDappCompositionManager
{
    private readonly IDappSeriesStore _seriesStore;
    private readonly IQuestStore _questStore;
    private readonly IQuestRunStore _runStore;
    private readonly IHolonStore _holonStore;
    private readonly ISTARManager _starManager;

    public DappCompositionManager(
        IDappSeriesStore seriesStore,
        IQuestStore questStore,
        IQuestRunStore runStore,
        IHolonStore holonStore,
        ISTARManager starManager)
    {
        _seriesStore = seriesStore;
        _questStore = questStore;
        _runStore = runStore;
        _holonStore = holonStore;
        _starManager = starManager;
    }

    // ── Series CRUD ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<DappSeries>> CreateAsync(
        Guid avatarId, DappSeriesCreateModel model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
            return Fail<DappSeries>("DappSeries.Name is required.");

        var series = DappSeries.NewDraft(avatarId, model.Name, model.Description);
        return await _seriesStore.UpsertSeriesAsync(series, ct);
    }

    public async Task<AZOAResult<DappSeries>> GetAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default)
    {
        var load = await _seriesStore.GetSeriesAsync(seriesId, ct);
        if (load.IsError || load.Result is null) return load;
        if (!load.Result.OwnedBy(avatarId)) return Fail<DappSeries>("Forbidden: series is owned by a different avatar.");
        return load;
    }

    public async Task<AZOAResult<IEnumerable<DappSeries>>> ListAsync(
        Guid avatarId, DappSeries.StatusKind? status = null, CancellationToken ct = default)
    {
        var load = await _seriesStore.GetSeriesByAvatarAsync(avatarId, ct);
        if (load.IsError || load.Result is null) return load;
        var filtered = status.HasValue
            ? load.Result.Where(s => s.Status == status.Value).ToList()
            : load.Result.ToList();
        return new AZOAResult<IEnumerable<DappSeries>> { Result = filtered, Message = "Success" };
    }

    public async Task<AZOAResult<DappSeries>> UpdateAsync(
        Guid seriesId, Guid avatarId, DappSeriesUpdateModel model, CancellationToken ct = default)
    {
        var load = await GetAsync(seriesId, avatarId, ct);
        if (load.IsError || load.Result is null) return load;
        var series = load.Result;

        if (model.Name is not null) series.Name = model.Name;
        if (model.Description is not null) series.Description = model.Description;
        if (model.TargetChain is not null) series.TargetChain = model.TargetChain;
        if (model.SharedConfig is not null) series.SharedConfigDict = model.SharedConfig;

        return await _seriesStore.UpsertSeriesAsync(series, ct);
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default)
    {
        var load = await GetAsync(seriesId, avatarId, ct);
        if (load.IsError || load.Result is null)
            return new AZOAResult<bool> { IsError = true, Message = load.Message };

        if (load.Result.Status != DappSeries.StatusKind.Draft
            && load.Result.Status != DappSeries.StatusKind.Archived)
        {
            return Fail<bool>($"Cannot delete series in status {load.Result.Status}; archive it first.");
        }

        return await _seriesStore.DeleteSeriesAsync(seriesId, ct);
    }

    // ── Quest Management within Series ───────────────────────────────────────

    public async Task<AZOAResult<DappSeriesQuest>> AddQuestAsync(
        Guid seriesId, Guid avatarId, DappSeriesAddQuestModel model, CancellationToken ct = default)
    {
        var seriesLoad = await GetAsync(seriesId, avatarId, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null)
            return Fail<DappSeriesQuest>(seriesLoad.Message);

        if (seriesLoad.Result.Status != DappSeries.StatusKind.Draft
            && seriesLoad.Result.Status != DappSeries.StatusKind.Building)
        {
            return Fail<DappSeriesQuest>($"Cannot add quests to series in status {seriesLoad.Result.Status}.");
        }

        if (model.Order < 1) return Fail<DappSeriesQuest>("Order must be 1-indexed (>= 1).");

        // Verify the referenced quest exists and is owned by the same avatar.
        var questLoad = await _questStore.GetQuestAsync(model.QuestId, ct);
        if (questLoad.IsError || questLoad.Result is null)
            return Fail<DappSeriesQuest>($"Quest {model.QuestId} not found.");
        if (questLoad.Result.AvatarId != avatarId)
            return Fail<DappSeriesQuest>("Forbidden: quest is owned by a different avatar.");

        // Reject a duplicate (series, quest) up front. See Managers/AGENTS.md §dapp-series-duplicates.
        var existing = await _seriesStore.GetQuestsBySeriesAsync(seriesId, ct);
        if (existing.IsError || existing.Result is null) return Fail<DappSeriesQuest>(existing.Message);
        if (existing.Result.Any(e => e.QuestIdGuid == model.QuestId))
            return Fail<DappSeriesQuest>($"Quest {model.QuestId} is already in series {seriesId}.");

        var entry = DappSeriesQuest.NewEntry(seriesId, model.QuestId, model.Order, model.InputMappings);
        return await _seriesStore.UpsertSeriesQuestAsync(entry, ct);
    }

    public async Task<AZOAResult<bool>> RemoveQuestAsync(
        Guid seriesId, Guid avatarId, Guid questId, CancellationToken ct = default)
    {
        var seriesLoad = await GetAsync(seriesId, avatarId, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null)
            return new AZOAResult<bool> { IsError = true, Message = seriesLoad.Message };

        return await _seriesStore.DeleteSeriesQuestAsync(seriesId, questId, ct);
    }

    public async Task<AZOAResult<DappSeriesQuest>> ReorderQuestAsync(
        Guid seriesId, Guid avatarId, Guid questId, int newOrder, CancellationToken ct = default)
    {
        if (newOrder < 1) return Fail<DappSeriesQuest>("Order must be 1-indexed (>= 1).");

        var seriesLoad = await GetAsync(seriesId, avatarId, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null)
            return Fail<DappSeriesQuest>(seriesLoad.Message);

        var entries = await _seriesStore.GetQuestsBySeriesAsync(seriesId, ct);
        if (entries.IsError || entries.Result is null) return Fail<DappSeriesQuest>(entries.Message);

        var target = entries.Result.FirstOrDefault(e => e.QuestIdGuid == questId);
        if (target is null) return Fail<DappSeriesQuest>($"Quest {questId} not in series.");

        target.Order = newOrder;
        return await _seriesStore.UpsertSeriesQuestAsync(target, ct);
    }

    public async Task<AZOAResult<DappSeriesQuest>> UpdateMappingsAsync(
        Guid seriesId, Guid avatarId, Guid questId, string? inputMappings, CancellationToken ct = default)
    {
        var seriesLoad = await GetAsync(seriesId, avatarId, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null)
            return Fail<DappSeriesQuest>(seriesLoad.Message);

        var entries = await _seriesStore.GetQuestsBySeriesAsync(seriesId, ct);
        if (entries.IsError || entries.Result is null) return Fail<DappSeriesQuest>(entries.Message);

        var target = entries.Result.FirstOrDefault(e => e.QuestIdGuid == questId);
        if (target is null) return Fail<DappSeriesQuest>($"Quest {questId} not in series.");

        if (inputMappings is not null)
        {
            // Validate JSON shape before persisting -- bad JSON should not land in
            // the store and silently break ComposeAsync downstream.
            try { _ = JsonSerializer.Deserialize<List<InputMapping>>(inputMappings); }
            catch (JsonException ex) { return Fail<DappSeriesQuest>($"Invalid InputMappings JSON: {ex.Message}"); }
        }
        target.InputMappings = inputMappings;
        return await _seriesStore.UpsertSeriesQuestAsync(target, ct);
    }

    public async Task<AZOAResult<IEnumerable<DappSeriesQuest>>> ListQuestsAsync(
        Guid seriesId, Guid avatarId, CancellationToken ct = default)
    {
        var seriesLoad = await GetAsync(seriesId, avatarId, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null)
            return new AZOAResult<IEnumerable<DappSeriesQuest>> { IsError = true, Message = seriesLoad.Message };
        return await _seriesStore.GetQuestsBySeriesAsync(seriesId, ct);
    }

    // ── Composition ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<CompositionValidationResult>> ValidateAsync(
        Guid seriesId, Guid avatarId, CancellationToken ct = default)
    {
        var seriesLoad = await GetAsync(seriesId, avatarId, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null)
            return Fail<CompositionValidationResult>(seriesLoad.Message);

        var entriesLoad = await _seriesStore.GetQuestsBySeriesAsync(seriesId, ct);
        if (entriesLoad.IsError || entriesLoad.Result is null)
            return Fail<CompositionValidationResult>(entriesLoad.Message);

        var entries = entriesLoad.Result.OrderBy(e => e.Order).ToList();
        if (entries.Count == 0)
            return Fail<CompositionValidationResult>("Series has no quests to compose.");

        // Pre-existing duplicate rows → clean validation failure, not a crash. See Managers/AGENTS.md §dapp-series-duplicates.
        var duplicateQuestIds = entries
            .GroupBy(e => e.QuestIdGuid)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateQuestIds.Count > 0)
        {
            var duplicateReport = new CompositionValidationResult { InputMappingConsistency = false, NoCircularDependencies = false };
            foreach (var dupId in duplicateQuestIds)
                duplicateReport.Diagnostics.Add($"Quest {dupId} appears more than once in the series; remove the duplicate entry.");
            return new AZOAResult<CompositionValidationResult> { Result = duplicateReport, Message = "One or more validation rules failed." };
        }

        var report = new CompositionValidationResult();

        // Load each quest definition once. The QuestIdGuid accessor handles
        // the storage-side Guid('N') hex string -> Guid conversion.
        var quests = new Dictionary<Guid, QuestDef>();
        foreach (var entry in entries)
        {
            var questGuid = entry.QuestIdGuid;
            var qLoad = await _questStore.GetQuestAsync(questGuid, ct);
            if (qLoad.IsError || qLoad.Result is null)
            {
                report.Diagnostics.Add($"Quest {questGuid} not found.");
                continue;
            }
            // Indexer assignment is last-writer-wins, consistent with OrderByQuestId
            // below; ValidateAsync rejects duplicate rows earlier regardless.
            quests[questGuid] = qLoad.Result;
        }

        await ValidateAllQuestsCompletedAsync(quests, report, ct);
        ValidateChainCompleteness(entries, quests, report);
        ValidateInputMappingConsistency(entries, quests, report);
        ValidateNoCircularDependencies(entries, quests, report);
        await ValidateHolonBindingsResolvedAsync(quests, report, ct);

        return new AZOAResult<CompositionValidationResult> { Result = report, Message = report.IsValid ? "Valid." : "One or more validation rules failed." };
    }

    public async Task<AZOAResult<DappManifest>> ComposeAsync(
        Guid seriesId, Guid avatarId, CancellationToken ct = default)
    {
        var validation = await ValidateAsync(seriesId, avatarId, ct);
        if (validation.IsError || validation.Result is null) return Fail<DappManifest>(validation.Message);
        if (!validation.Result.IsValid)
            return Fail<DappManifest>($"Composition validation failed: {string.Join("; ", validation.Result.Diagnostics)}");

        var series = (await GetAsync(seriesId, avatarId, ct)).Result!;
        var entries = (await _seriesStore.GetQuestsBySeriesAsync(seriesId, ct)).Result!.OrderBy(e => e.Order).ToList();

        // Re-load quest defs (cheap; small N in practice).
        var quests = new List<QuestDef>();
        foreach (var entry in entries)
        {
            var qLoad = await _questStore.GetQuestAsync(entry.QuestIdGuid, ct);
            if (!qLoad.IsError && qLoad.Result is not null) quests.Add(qLoad.Result);
        }

        var boundHolonIds = ExtractBoundHolonIds(quests).ToList();
        var combinedConfig = new Dictionary<string, string>(series.SharedConfigDict);

        var manifest = new DappManifest
        {
            DappSeriesId = seriesId,
            BoundHolonIds = boundHolonIds,
            QuestGraph = SerializeQuestGraph(entries, quests),
            TargetChain = series.TargetChain ?? string.Empty,
            Config = combinedConfig,
            GeneratedDate = DateTime.UtcNow,
        };

        // Persist manifest + flip status to Building so subsequent calls see
        // the latest composed artifact.
        series.Manifest = JsonSerializer.Serialize(manifest);
        series.Status = DappSeries.StatusKind.Building;
        var upsert = await _seriesStore.UpsertSeriesAsync(series, ct);
        if (upsert.IsError) return Fail<DappManifest>(upsert.Message);

        return new AZOAResult<DappManifest> { Result = manifest, Message = "Composed." };
    }

    // ── Generation & Deployment ──────────────────────────────────────────────

    public async Task<AZOAResult<ISTARODK>> GenerateAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default)
    {
        var composeResult = await ComposeAsync(seriesId, avatarId, ct);
        if (composeResult.IsError || composeResult.Result is null) return Fail<ISTARODK>(composeResult.Message);
        var manifest = composeResult.Result;

        var series = (await GetAsync(seriesId, avatarId, ct)).Result!;

        // STARODKCreateModel does not yet carry TargetChain/BoundHolonIds; those
        // flow to ISTARManager via STARDappGenerationRequest on the Generate
        // call. spec.md §STAR Integration Flow has those fields on the create
        // model which does not match the runtime ISTARManager surface -- this
        // is the authoritative shape.
        var starCreate = new STARODKCreateModel
        {
            Name = series.Name,
            Description = series.Description ?? string.Empty,
            AvatarId = avatarId,
        };
        var starUpsert = await _starManager.CreateOrUpdateAsync(starCreate, avatarId);
        if (starUpsert.IsError || starUpsert.Result is null) return Fail<ISTARODK>(starUpsert.Message);

        var generationRequest = new STARDappGenerationRequest
        {
            TargetChain = manifest.TargetChain,
            BoundHolonIds = manifest.BoundHolonIds,
            Config = manifest.Config,
        };
        var generated = await _starManager.GenerateAsync(starUpsert.Result.Id, generationRequest, avatarId);
        if (generated.IsError || generated.Result is null) return Fail<ISTARODK>(generated.Message);

        series.StarOdkIdGuid = generated.Result.Id;
        series.Status = DappSeries.StatusKind.Ready;
        var save = await _seriesStore.UpsertSeriesAsync(series, ct);
        if (save.IsError) return Fail<ISTARODK>(save.Message);

        return generated;
    }

    public async Task<AZOAResult<ISTARODK>> DeployAsync(
        Guid seriesId, Guid avatarId, string? targetOverride = null, CancellationToken ct = default)
    {
        var seriesLoad = await GetAsync(seriesId, avatarId, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null) return Fail<ISTARODK>(seriesLoad.Message);
        var series = seriesLoad.Result;

        if (series.Status != DappSeries.StatusKind.Ready)
            return Fail<ISTARODK>($"Series must be in Ready status to deploy; currently {series.Status}.");
        var starId = series.StarOdkIdGuid;
        if (starId is null)
            return Fail<ISTARODK>("Series has no associated STARODK; call Generate first.");

        // targetOverride is currently passed-through informationally; the
        // ISTARManager.DeployAsync surface does not yet accept per-call target
        // overrides -- if a different chain is needed, re-run Generate with an
        // updated TargetChain on the series. Tracked as a follow-up.
        var deployed = await _starManager.DeployAsync(starId.Value, avatarId);
        if (deployed.IsError || deployed.Result is null) return Fail<ISTARODK>(deployed.Message);

        series.Status = DappSeries.StatusKind.Deployed;
        series.DeployedDate = DateTimeOffset.UtcNow;
        var save = await _seriesStore.UpsertSeriesAsync(series, ct);
        if (save.IsError) return Fail<ISTARODK>(save.Message);

        return deployed;
    }

    // ── Validators ───────────────────────────────────────────────────────────

    private async Task ValidateAllQuestsCompletedAsync(
        Dictionary<Guid, QuestDef> quests, CompositionValidationResult report, CancellationToken ct)
    {
        report.AllQuestsCompleted = true;
        foreach (var (questId, _) in quests)
        {
            var runs = await _runStore.GetByQuestIdAsync(questId, ct);
            if (runs.IsError || runs.Result is null)
            {
                report.AllQuestsCompleted = false;
                report.Diagnostics.Add($"Quest {questId}: failed to load runs ({runs.Message}).");
                continue;
            }
            var latest = runs.Result.OrderByDescending(r => r.StartedAt).FirstOrDefault();
            if (latest is null)
            {
                report.AllQuestsCompleted = false;
                report.Diagnostics.Add($"Quest {questId}: no QuestRun rows -- quest has never been executed.");
                continue;
            }
            if (latest.Status != QuestRunStatus.Succeeded)
            {
                report.AllQuestsCompleted = false;
                report.Diagnostics.Add($"Quest {questId}: latest QuestRun status is {latest.Status}, expected Succeeded.");
            }
        }
    }

    private static void ValidateChainCompleteness(
        List<DappSeriesQuest> entries, Dictionary<Guid, QuestDef> quests, CompositionValidationResult report)
    {
        report.ChainCompleteness = true;
        var priorQuestIds = new HashSet<Guid>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (!Guid.TryParseExact(entries[i].QuestId, "N", out var questGuid)) continue;
            if (!quests.TryGetValue(questGuid, out var quest)) continue;

            if (i > 0)
            {
                // After the first quest, the quest must depend on at least one
                // prior quest in the series.
                var hasPriorDep = quest.Dependencies.Any(d => priorQuestIds.Contains(d.DependsOnQuestId));
                if (!hasPriorDep)
                {
                    report.ChainCompleteness = false;
                    report.Diagnostics.Add(
                        $"Quest {questGuid} (order {entries[i].Order}): no QuestDependency on any prior quest in the series.");
                }
            }
            priorQuestIds.Add(questGuid);
        }
    }

    private static void ValidateInputMappingConsistency(
        List<DappSeriesQuest> entries, Dictionary<Guid, QuestDef> quests, CompositionValidationResult report)
    {
        report.InputMappingConsistency = true;
        var orderByQuestId = OrderByQuestId(entries);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.InputMappings)) continue;
            List<InputMapping>? mappings;
            try
            {
                mappings = JsonSerializer.Deserialize<List<InputMapping>>(entry.InputMappings);
            }
            catch (JsonException ex)
            {
                report.InputMappingConsistency = false;
                report.Diagnostics.Add($"Entry {entry.Id}: malformed InputMappings JSON ({ex.Message}).");
                continue;
            }
            if (mappings is null) continue;

            foreach (var mapping in mappings)
            {
                if (!orderByQuestId.TryGetValue(mapping.SourceQuestId, out var sourceOrder))
                {
                    report.InputMappingConsistency = false;
                    report.Diagnostics.Add($"Entry {entry.Id}: InputMapping source quest {mapping.SourceQuestId} not in series.");
                    continue;
                }
                if (!orderByQuestId.TryGetValue(mapping.TargetQuestId, out var targetOrder))
                {
                    report.InputMappingConsistency = false;
                    report.Diagnostics.Add($"Entry {entry.Id}: InputMapping target quest {mapping.TargetQuestId} not in series.");
                    continue;
                }
                if (sourceOrder >= targetOrder)
                {
                    report.InputMappingConsistency = false;
                    report.Diagnostics.Add(
                        $"Entry {entry.Id}: InputMapping source ({sourceOrder}) must come before target ({targetOrder}).");
                }
                // Verify the referenced nodes exist on the source/target quests.
                if (quests.TryGetValue(mapping.SourceQuestId, out var sourceQuest)
                    && sourceQuest.Nodes.All(n => n.Id != mapping.SourceNodeId))
                {
                    report.InputMappingConsistency = false;
                    report.Diagnostics.Add(
                        $"Entry {entry.Id}: source node {mapping.SourceNodeId} not found in quest {mapping.SourceQuestId}.");
                }
                if (quests.TryGetValue(mapping.TargetQuestId, out var targetQuest)
                    && targetQuest.Nodes.All(n => n.Id != mapping.TargetNodeId))
                {
                    report.InputMappingConsistency = false;
                    report.Diagnostics.Add(
                        $"Entry {entry.Id}: target node {mapping.TargetNodeId} not found in quest {mapping.TargetQuestId}.");
                }
            }
        }
    }

    private static void ValidateNoCircularDependencies(
        List<DappSeriesQuest> entries, Dictionary<Guid, QuestDef> quests, CompositionValidationResult report)
    {
        report.NoCircularDependencies = true;
        var orderByQuestId = OrderByQuestId(entries);

        foreach (var (questGuid, quest) in quests)
        {
            if (!orderByQuestId.TryGetValue(questGuid, out var thisOrder)) continue;
            foreach (var dep in quest.Dependencies)
            {
                if (orderByQuestId.TryGetValue(dep.DependsOnQuestId, out var depOrder)
                    && depOrder >= thisOrder)
                {
                    report.NoCircularDependencies = false;
                    report.Diagnostics.Add(
                        $"Quest {questGuid} (order {thisOrder}) depends on quest {dep.DependsOnQuestId} which appears later (order {depOrder}).");
                }
            }
        }
    }

    private async Task ValidateHolonBindingsResolvedAsync(
        Dictionary<Guid, QuestDef> quests, CompositionValidationResult report, CancellationToken ct)
    {
        report.HolonBindingsResolved = true;
        var allHolonRefs = ExtractBoundHolonIds(quests.Values).Distinct().ToList();
        foreach (var holonId in allHolonRefs)
        {
            var holon = await _holonStore.GetByIdAsync(holonId, ct);
            if (holon.IsError || holon.Result is null)
            {
                report.HolonBindingsResolved = false;
                report.Diagnostics.Add($"Referenced holon {holonId} not found.");
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Duplicate-tolerant order lookup (last-writer-wins). See Managers/AGENTS.md §dapp-series-duplicates.
    private static Dictionary<Guid, int> OrderByQuestId(IEnumerable<DappSeriesQuest> entries)
    {
        var map = new Dictionary<Guid, int>();
        foreach (var e in entries) map[e.QuestIdGuid] = (int)e.Order;
        return map;
    }

    private static IEnumerable<Guid> ExtractBoundHolonIds(IEnumerable<QuestDef> quests)
    {
        // Scan each node's Config JSON for any property named *holonId*
        // (case-insensitive) holding a Guid value. Covers the holon-touching
        // QuestNodeType configs (HolonCreate, HolonUpdate, HolonGet, etc.)
        // without needing per-node-type config schema awareness.
        var ids = new HashSet<Guid>();
        foreach (var quest in quests)
        {
            foreach (var node in quest.Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Config)) continue;
                JsonElement root;
                try
                {
                    using var doc = JsonDocument.Parse(node.Config);
                    root = doc.RootElement.Clone();
                }
                catch (JsonException) { continue; }
                foreach (var guid in ScanForGuids(root, propertyNameFilter: "holon"))
                    ids.Add(guid);
            }
        }
        return ids;
    }

    private static IEnumerable<Guid> ScanForGuids(JsonElement element, string propertyNameFilter)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var nameMatches = prop.Name.Contains(propertyNameFilter, StringComparison.OrdinalIgnoreCase);

                    // Scalar string value under a *holon*-named property.
                    if (nameMatches
                        && prop.Value.ValueKind == JsonValueKind.String
                        && Guid.TryParse(prop.Value.GetString(), out var scalarGuid))
                    {
                        yield return scalarGuid;
                    }

                    // FR-5 / AC-5: array value under a *holon*-named property
                    // (e.g. GateCheckNodeConfig.Holons serialises as
                    // "holons": ["<guid>", "<guid>", ...]). Yield each Guid element.
                    if (nameMatches && prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var arrayItem in prop.Value.EnumerateArray())
                        {
                            if (arrayItem.ValueKind == JsonValueKind.String
                                && Guid.TryParse(arrayItem.GetString(), out var arrayGuid))
                                yield return arrayGuid;
                        }
                    }

                    foreach (var nested in ScanForGuids(prop.Value, propertyNameFilter))
                        yield return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var nested in ScanForGuids(item, propertyNameFilter))
                        yield return nested;
                break;
        }
    }

    private static string SerializeQuestGraph(
        List<DappSeriesQuest> entries, List<QuestDef> quests)
    {
        var graph = entries.Select(entry =>
        {
            var qid = entry.QuestIdGuid;
            var quest = quests.FirstOrDefault(q => q.Id == qid);
            return new
            {
                order = entry.Order,
                questId = qid,
                name = quest?.Name,
                dependencies = quest?.Dependencies.Select(d => new
                {
                    dependsOnQuestId = d.DependsOnQuestId,
                    dependsOnNodeId = d.DependsOnNodeId,
                    type = d.DependencyType.ToString(),
                }).ToList(),
                inputMappings = entry.InputMappings,
            };
        });
        return JsonSerializer.Serialize(graph);
    }

    private static AZOAResult<T> Fail<T>(string message) =>
        new() { IsError = true, Message = message };
}
