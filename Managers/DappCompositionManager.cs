using System.Text.Json;
using AZOA.WebAPI.Core.Json;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
// Disambiguate `ActionDefinition`: the source-gen'd POCO (Generated.SurrealDb.ActionDefinition) is
// not yet wired through IActionStore -- the hand-written model stays in
// service until the action cutover slice lands. Alias keeps the manager
// readable without scattering fully-qualified type names everywhere.
using ActionDef = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Managers;

public sealed class DappCompositionManager : IDappCompositionManager
{
    private readonly IDappSeriesStore _seriesStore;
    private readonly IActionStore _actionStore;
    private readonly IActionRunStore _runStore;
    private readonly IHolonStore _holonStore;
    private readonly ISTARManager _starManager;

    public DappCompositionManager(
        IDappSeriesStore seriesStore,
        IActionStore actionStore,
        IActionRunStore runStore,
        IHolonStore holonStore,
        ISTARManager starManager)
    {
        _seriesStore = seriesStore;
        _actionStore = actionStore;
        _runStore = runStore;
        _holonStore = holonStore;
        _starManager = starManager;
    }

    // ── Series CRUD ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<DappSeries>> CreateAsync(
        Guid userId, DappSeriesCreateModel model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
            return Fail<DappSeries>("DappSeries.Name is required.");

        var series = DappSeries.NewDraft(userId, model.Name, model.Description);
        return await _seriesStore.UpsertSeriesAsync(series, ct);
    }

    public async Task<AZOAResult<DappSeries>> GetAsync(Guid seriesId, Guid userId, CancellationToken ct = default)
    {
        var load = await _seriesStore.GetSeriesAsync(seriesId, ct);
        if (load.IsError || load.Result is null) return load;
        if (!load.Result.OwnedBy(userId)) return Fail<DappSeries>("Forbidden: series is owned by a different user.");
        return load;
    }

    public async Task<AZOAResult<IEnumerable<DappSeries>>> ListAsync(
        Guid userId, DappSeries.StatusKind? status = null, CancellationToken ct = default)
    {
        var load = await _seriesStore.GetSeriesByUserAsync(userId, ct);
        if (load.IsError || load.Result is null) return load;
        var filtered = status.HasValue
            ? load.Result.Where(s => s.Status == status.Value).ToList()
            : load.Result.ToList();
        return new AZOAResult<IEnumerable<DappSeries>> { Result = filtered, Message = "Success" };
    }

    public async Task<AZOAResult<DappSeries>> UpdateAsync(
        Guid seriesId, Guid userId, DappSeriesUpdateModel model, CancellationToken ct = default)
    {
        var load = await GetAsync(seriesId, userId, ct);
        if (load.IsError || load.Result is null) return load;
        var series = load.Result;

        if (model.Name is not null) series.Name = model.Name;
        if (model.Description is not null) series.Description = model.Description;
        if (model.TargetChain is not null) series.TargetChain = model.TargetChain;
        if (model.SharedConfig is not null) series.SharedConfigDict = model.SharedConfig;

        return await _seriesStore.UpsertSeriesAsync(series, ct);
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid seriesId, Guid userId, CancellationToken ct = default)
    {
        var load = await GetAsync(seriesId, userId, ct);
        if (load.IsError || load.Result is null)
            return new AZOAResult<bool> { IsError = true, Message = load.Message };

        if (load.Result.Status != DappSeries.StatusKind.Draft
            && load.Result.Status != DappSeries.StatusKind.Archived)
        {
            return Fail<bool>($"Cannot delete series in status {load.Result.Status}; archive it first.");
        }

        return await _seriesStore.DeleteSeriesAsync(seriesId, ct);
    }

    // ── Action Management within Series ─────────────────────────────────────────────────

    public async Task<AZOAResult<DappSeriesAction>> AddActionAsync(
        Guid seriesId, Guid userId, DappSeriesAddActionModel model, CancellationToken ct = default)
    {
        var seriesLoad = await GetAsync(seriesId, userId, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null)
            return Fail<DappSeriesAction>(seriesLoad.Message);

        if (seriesLoad.Result.Status != DappSeries.StatusKind.Draft
            && seriesLoad.Result.Status != DappSeries.StatusKind.Building)
        {
            return Fail<DappSeriesAction>($"Cannot add actions to series in status {seriesLoad.Result.Status}.");
        }

        if (model.Order < 1) return Fail<DappSeriesAction>("Order must be 1-indexed (>= 1).");

        // Verify the referenced action exists and is owned by the same user.
        var actionLoad = await _actionStore.GetActionAsync(model.ActionId, ct);
        if (actionLoad.IsError || actionLoad.Result is null)
            return Fail<DappSeriesAction>($"Action {model.ActionId} not found.");
        if (actionLoad.Result.UserId != userId)
            return Fail<DappSeriesAction>("Forbidden: action is owned by a different user.");

        // Reject a duplicate (series, action) up front. See Managers/AGENTS.md §dapp-series-duplicates.
        var existing = await _seriesStore.GetActionsBySeriesAsync(seriesId, ct);
        if (existing.IsError || existing.Result is null) return Fail<DappSeriesAction>(existing.Message);
        if (existing.Result.Any(e => e.ActionIdGuid == model.ActionId))
            return Fail<DappSeriesAction>($"Action {model.ActionId} is already in series {seriesId}.");

        var entry = DappSeriesAction.NewEntry(seriesId, model.ActionId, model.Order, model.InputMappings);
        return await _seriesStore.UpsertSeriesActionAsync(entry, ct);
    }

    public async Task<AZOAResult<bool>> RemoveActionAsync(
        Guid seriesId, Guid userId, Guid actionId, CancellationToken ct = default)
    {
        var seriesLoad = await GetAsync(seriesId, userId, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null)
            return new AZOAResult<bool> { IsError = true, Message = seriesLoad.Message };

        return await _seriesStore.DeleteSeriesActionAsync(seriesId, actionId, ct);
    }

    public async Task<AZOAResult<DappSeriesAction>> ReorderActionAsync(
        Guid seriesId, Guid userId, Guid actionId, int newOrder, CancellationToken ct = default)
    {
        if (newOrder < 1) return Fail<DappSeriesAction>("Order must be 1-indexed (>= 1).");

        var seriesLoad = await GetAsync(seriesId, userId, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null)
            return Fail<DappSeriesAction>(seriesLoad.Message);

        var entries = await _seriesStore.GetActionsBySeriesAsync(seriesId, ct);
        if (entries.IsError || entries.Result is null) return Fail<DappSeriesAction>(entries.Message);

        var target = entries.Result.FirstOrDefault(e => e.ActionIdGuid == actionId);
        if (target is null) return Fail<DappSeriesAction>($"Action {actionId} not in series.");

        target.Order = newOrder;
        return await _seriesStore.UpsertSeriesActionAsync(target, ct);
    }

    public async Task<AZOAResult<DappSeriesAction>> UpdateMappingsAsync(
        Guid seriesId, Guid userId, Guid actionId, string? inputMappings, CancellationToken ct = default)
    {
        var seriesLoad = await GetAsync(seriesId, userId, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null)
            return Fail<DappSeriesAction>(seriesLoad.Message);

        var entries = await _seriesStore.GetActionsBySeriesAsync(seriesId, ct);
        if (entries.IsError || entries.Result is null) return Fail<DappSeriesAction>(entries.Message);

        var target = entries.Result.FirstOrDefault(e => e.ActionIdGuid == actionId);
        if (target is null) return Fail<DappSeriesAction>($"Action {actionId} not in series.");

        if (inputMappings is not null)
        {
            // Validate JSON shape before persisting -- bad JSON should not land in
            // the store and silently break ComposeAsync downstream.
            try { _ = JsonSerializer.Deserialize<List<InputMapping>>(inputMappings); }
            catch (JsonException ex) { return Fail<DappSeriesAction>($"Invalid InputMappings JSON: {ex.Message}"); }
        }
        target.InputMappings = inputMappings;
        return await _seriesStore.UpsertSeriesActionAsync(target, ct);
    }

    public async Task<AZOAResult<IEnumerable<DappSeriesAction>>> ListActionsAsync(
        Guid seriesId, Guid userId, CancellationToken ct = default)
    {
        var seriesLoad = await GetAsync(seriesId, userId, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null)
            return new AZOAResult<IEnumerable<DappSeriesAction>> { IsError = true, Message = seriesLoad.Message };
        return await _seriesStore.GetActionsBySeriesAsync(seriesId, ct);
    }

    // ── Composition ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<CompositionValidationResult>> ValidateAsync(
        Guid seriesId, Guid userId, CancellationToken ct = default)
    {
        var seriesLoad = await GetAsync(seriesId, userId, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null)
            return Fail<CompositionValidationResult>(seriesLoad.Message);

        var entriesLoad = await _seriesStore.GetActionsBySeriesAsync(seriesId, ct);
        if (entriesLoad.IsError || entriesLoad.Result is null)
            return Fail<CompositionValidationResult>(entriesLoad.Message);

        var entries = entriesLoad.Result.OrderBy(e => e.Order).ToList();
        if (entries.Count == 0)
            return Fail<CompositionValidationResult>("Series has no actions to compose.");

        // Pre-existing duplicate rows → clean validation failure, not a crash. See Managers/AGENTS.md §dapp-series-duplicates.
        var duplicateActionIds = entries
            .GroupBy(e => e.ActionIdGuid)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateActionIds.Count > 0)
        {
            var duplicateReport = new CompositionValidationResult { InputMappingConsistency = false, NoCircularDependencies = false };
            foreach (var dupId in duplicateActionIds)
                duplicateReport.Diagnostics.Add($"Action {dupId} appears more than once in the series; remove the duplicate entry.");
            return new AZOAResult<CompositionValidationResult> { Result = duplicateReport, Message = "One or more validation rules failed." };
        }

        var report = new CompositionValidationResult();

        // Load each action definition once. The ActionIdGuid accessor handles
        // the storage-side Guid('N') hex string -> Guid conversion.
        var actions = new Dictionary<Guid, ActionDef>();
        foreach (var entry in entries)
        {
            var actionGuid = entry.ActionIdGuid;
            var qLoad = await _actionStore.GetActionAsync(actionGuid, ct);
            if (qLoad.IsError || qLoad.Result is null)
            {
                report.Diagnostics.Add($"Action {actionGuid} not found.");
                continue;
            }
            // Indexer assignment is last-writer-wins, consistent with OrderByActionId
            // below; ValidateAsync rejects duplicate rows earlier regardless.
            actions[actionGuid] = qLoad.Result;
        }

        await ValidateAllActionsCompletedAsync(actions, report, ct);
        ValidateChainCompleteness(entries, actions, report);
        ValidateInputMappingConsistency(entries, actions, report);
        ValidateNoCircularDependencies(entries, actions, report);
        await ValidateHolonBindingsResolvedAsync(actions, report, ct);

        return new AZOAResult<CompositionValidationResult> { Result = report, Message = report.IsValid ? "Valid." : "One or more validation rules failed." };
    }

    public async Task<AZOAResult<DappManifest>> ComposeAsync(
        Guid seriesId, Guid userId, CancellationToken ct = default)
    {
        var validation = await ValidateAsync(seriesId, userId, ct);
        if (validation.IsError || validation.Result is null) return Fail<DappManifest>(validation.Message);
        if (!validation.Result.IsValid)
            return Fail<DappManifest>($"Composition validation failed: {string.Join("; ", validation.Result.Diagnostics)}");

        var series = (await GetAsync(seriesId, userId, ct)).Result!;
        var entries = (await _seriesStore.GetActionsBySeriesAsync(seriesId, ct)).Result!.OrderBy(e => e.Order).ToList();

        // Re-load action defs (cheap; small N in practice).
        var actions = new List<ActionDef>();
        foreach (var entry in entries)
        {
            var qLoad = await _actionStore.GetActionAsync(entry.ActionIdGuid, ct);
            if (!qLoad.IsError && qLoad.Result is not null) actions.Add(qLoad.Result);
        }

        var boundHolonIds = ExtractBoundHolonIds(actions).ToList();
        var combinedConfig = new Dictionary<string, string>(series.SharedConfigDict);

        var manifest = new DappManifest
        {
            DappSeriesId = seriesId,
            BoundHolonIds = boundHolonIds,
            ActionGraph = SerializeActionGraph(entries, actions),
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

    public async Task<AZOAResult<ISTARODK>> GenerateAsync(Guid seriesId, Guid userId, CancellationToken ct = default)
    {
        var composeResult = await ComposeAsync(seriesId, userId, ct);
        if (composeResult.IsError || composeResult.Result is null) return Fail<ISTARODK>(composeResult.Message);
        var manifest = composeResult.Result;

        var series = (await GetAsync(seriesId, userId, ct)).Result!;

        // STARODKCreateModel does not yet carry TargetChain/BoundHolonIds; those
        // flow to ISTARManager via STARDappGenerationRequest on the Generate
        // call. spec.md §STAR Integration Flow has those fields on the create
        // model which does not match the runtime ISTARManager surface -- this
        // is the authoritative shape.
        var starCreate = new STARODKCreateModel
        {
            Name = series.Name,
            Description = series.Description ?? string.Empty,
            UserId = userId,
        };
        var starUpsert = await _starManager.CreateOrUpdateAsync(starCreate, userId);
        if (starUpsert.IsError || starUpsert.Result is null) return Fail<ISTARODK>(starUpsert.Message);

        var generationRequest = new STARDappGenerationRequest
        {
            TargetChain = manifest.TargetChain,
            BoundHolonIds = manifest.BoundHolonIds,
            Config = manifest.Config,
        };
        var generated = await _starManager.GenerateAsync(starUpsert.Result.Id, generationRequest, userId);
        if (generated.IsError || generated.Result is null) return Fail<ISTARODK>(generated.Message);

        series.StarOdkIdGuid = generated.Result.Id;
        series.Status = DappSeries.StatusKind.Ready;
        var save = await _seriesStore.UpsertSeriesAsync(series, ct);
        if (save.IsError) return Fail<ISTARODK>(save.Message);

        return generated;
    }

    public async Task<AZOAResult<ISTARODK>> DeployAsync(
        Guid seriesId, Guid userId, string? targetOverride = null, CancellationToken ct = default)
    {
        var seriesLoad = await GetAsync(seriesId, userId, ct);
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
        var deployed = await _starManager.DeployAsync(starId.Value, userId);
        if (deployed.IsError || deployed.Result is null) return Fail<ISTARODK>(deployed.Message);

        series.Status = DappSeries.StatusKind.Deployed;
        series.DeployedDate = DateTimeOffset.UtcNow;
        var save = await _seriesStore.UpsertSeriesAsync(series, ct);
        if (save.IsError) return Fail<ISTARODK>(save.Message);

        return deployed;
    }

    // ── Validators ───────────────────────────────────────────────────────────

    private async Task ValidateAllActionsCompletedAsync(
        Dictionary<Guid, ActionDef> actions, CompositionValidationResult report, CancellationToken ct)
    {
        report.AllActionsCompleted = true;
        foreach (var (actionId, _) in actions)
        {
            var runs = await _runStore.GetByActionIdAsync(actionId, ct);
            if (runs.IsError || runs.Result is null)
            {
                report.AllActionsCompleted = false;
                report.Diagnostics.Add($"Action {actionId}: failed to load runs ({runs.Message}).");
                continue;
            }
            var latest = runs.Result.OrderByDescending(r => r.StartedAt).FirstOrDefault();
            if (latest is null)
            {
                report.AllActionsCompleted = false;
                report.Diagnostics.Add($"Action {actionId}: no ActionRun rows -- action has never been executed.");
                continue;
            }
            if (latest.Status != ActionRunStatus.Succeeded)
            {
                report.AllActionsCompleted = false;
                report.Diagnostics.Add($"Action {actionId}: latest ActionRun status is {latest.Status}, expected Succeeded.");
            }
        }
    }

    private static void ValidateChainCompleteness(
        List<DappSeriesAction> entries, Dictionary<Guid, ActionDef> actions, CompositionValidationResult report)
    {
        report.ChainCompleteness = true;
        var priorActionIds = new HashSet<Guid>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (!Guid.TryParseExact(entries[i].ActionId, "N", out var actionGuid)) continue;
            if (!actions.TryGetValue(actionGuid, out var action)) continue;

            if (i > 0)
            {
                // After the first action, the action must depend on at least one
                // prior action in the series.
                var hasPriorDep = action.Dependencies.Any(d => priorActionIds.Contains(d.DependsOnActionId));
                if (!hasPriorDep)
                {
                    report.ChainCompleteness = false;
                    report.Diagnostics.Add(
                        $"Action {actionGuid} (order {entries[i].Order}): no ActionDependency on any prior action in the series.");
                }
            }
            priorActionIds.Add(actionGuid);
        }
    }

    private static void ValidateInputMappingConsistency(
        List<DappSeriesAction> entries, Dictionary<Guid, ActionDef> actions, CompositionValidationResult report)
    {
        report.InputMappingConsistency = true;
        var orderByActionId = OrderByActionId(entries);

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
                if (!orderByActionId.TryGetValue(mapping.SourceActionId, out var sourceOrder))
                {
                    report.InputMappingConsistency = false;
                    report.Diagnostics.Add($"Entry {entry.Id}: InputMapping source action {mapping.SourceActionId} not in series.");
                    continue;
                }
                if (!orderByActionId.TryGetValue(mapping.TargetActionId, out var targetOrder))
                {
                    report.InputMappingConsistency = false;
                    report.Diagnostics.Add($"Entry {entry.Id}: InputMapping target action {mapping.TargetActionId} not in series.");
                    continue;
                }
                if (sourceOrder >= targetOrder)
                {
                    report.InputMappingConsistency = false;
                    report.Diagnostics.Add(
                        $"Entry {entry.Id}: InputMapping source ({sourceOrder}) must come before target ({targetOrder}).");
                }
                // Verify the referenced nodes exist on the source/target actions.
                if (actions.TryGetValue(mapping.SourceActionId, out var sourceAction)
                    && sourceAction.Nodes.All(n => n.Id != mapping.SourceNodeId))
                {
                    report.InputMappingConsistency = false;
                    report.Diagnostics.Add(
                        $"Entry {entry.Id}: source node {mapping.SourceNodeId} not found in action {mapping.SourceActionId}.");
                }
                if (actions.TryGetValue(mapping.TargetActionId, out var targetAction)
                    && targetAction.Nodes.All(n => n.Id != mapping.TargetNodeId))
                {
                    report.InputMappingConsistency = false;
                    report.Diagnostics.Add(
                        $"Entry {entry.Id}: target node {mapping.TargetNodeId} not found in action {mapping.TargetActionId}.");
                }
            }
        }
    }

    private static void ValidateNoCircularDependencies(
        List<DappSeriesAction> entries, Dictionary<Guid, ActionDef> actions, CompositionValidationResult report)
    {
        report.NoCircularDependencies = true;
        var orderByActionId = OrderByActionId(entries);

        foreach (var (actionGuid, action) in actions)
        {
            if (!orderByActionId.TryGetValue(actionGuid, out var thisOrder)) continue;
            foreach (var dep in action.Dependencies)
            {
                if (orderByActionId.TryGetValue(dep.DependsOnActionId, out var depOrder)
                    && depOrder >= thisOrder)
                {
                    report.NoCircularDependencies = false;
                    report.Diagnostics.Add(
                        $"Action {actionGuid} (order {thisOrder}) depends on action {dep.DependsOnActionId} which appears later (order {depOrder}).");
                }
            }
        }
    }

    private async Task ValidateHolonBindingsResolvedAsync(
        Dictionary<Guid, ActionDef> actions, CompositionValidationResult report, CancellationToken ct)
    {
        report.HolonBindingsResolved = true;
        var allHolonRefs = ExtractBoundHolonIds(actions.Values).Distinct().ToList();
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
    private static Dictionary<Guid, int> OrderByActionId(IEnumerable<DappSeriesAction> entries)
    {
        var map = new Dictionary<Guid, int>();
        foreach (var e in entries) map[e.ActionIdGuid] = (int)e.Order;
        return map;
    }

    private static IEnumerable<Guid> ExtractBoundHolonIds(IEnumerable<ActionDef> actions)
    {
        // Scan each node's Config JSON for any property named *holonId*
        // (case-insensitive) holding a Guid value. Covers the holon-touching
        // ActionNodeType configs (HolonCreate, HolonUpdate, HolonGet, etc.)
        // without needing per-node-type config schema awareness.
        var ids = new HashSet<Guid>();
        foreach (var action in actions)
        {
            foreach (var node in action.Nodes)
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

    private static string SerializeActionGraph(
        List<DappSeriesAction> entries, List<ActionDef> actions)
    {
        var graph = entries.Select(entry =>
        {
            var qid = entry.ActionIdGuid;
            var action = actions.FirstOrDefault(q => q.Id == qid);
            return new
            {
                order = entry.Order,
                actionId = qid,
                name = action?.Name,
                dependencies = action?.Dependencies.Select(d => new
                {
                    dependsOnActionId = d.DependsOnActionId,
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
