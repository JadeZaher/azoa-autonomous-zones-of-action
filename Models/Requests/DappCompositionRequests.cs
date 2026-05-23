namespace OASIS.WebAPI.Models.Requests;

/// <summary>
/// Caller-facing request DTOs and composed artifacts for the
/// <c>dapp-composition</c> track. The persisted entities live as generated
/// POCOs in <c>OASIS.WebAPI.Generated.SurrealDb.{DappSeries,DappSeriesQuest}</c>;
/// these DTOs are the API surface that converts to/from those POCOs at the
/// manager seam.
/// </summary>
public class DappSeriesCreateModel
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class DappSeriesUpdateModel
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? TargetChain { get; set; }
    public Dictionary<string, string>? SharedConfig { get; set; }
}

public class DappSeriesAddQuestModel
{
    public Guid QuestId { get; set; }
    public int Order { get; set; }

    /// <summary>JSON array of <see cref="InputMapping"/> entries; null when no cross-quest flow.</summary>
    public string? InputMappings { get; set; }
}

public class DappSeriesReorderQuestModel
{
    public int NewOrder { get; set; }
}

public class DappSeriesUpdateMappingsModel
{
    public string? InputMappings { get; set; }
}

/// <summary>
/// One cross-quest data-flow rule. A series may carry many of these on each
/// <c>DappSeriesQuest.InputMappings</c> slot. The shape mirrors the JSON
/// example in <c>dapp-composition/spec.md §InputMapping</c>.
/// </summary>
public class InputMapping
{
    public Guid SourceQuestId { get; set; }
    public Guid SourceNodeId { get; set; }
    public Guid TargetQuestId { get; set; }
    public Guid TargetNodeId { get; set; }

    /// <summary>Field rename map: source-output field -> target-input field.</summary>
    public Dictionary<string, string> FieldMap { get; set; } = new();
}

/// <summary>
/// Composed artifact produced by <c>ComposeAsync</c>. Persisted as a JSON
/// string on <c>dapp_series.manifest</c> -- NOT a separate aggregate root
/// because it is always read whole alongside its parent series.
/// </summary>
public class DappManifest
{
    public Guid DappSeriesId { get; set; }
    public string Version { get; set; } = "1.0.0";

    /// <summary>Deduplicated set of holon ids referenced by every quest node in the series.</summary>
    public List<Guid> BoundHolonIds { get; set; } = new();

    /// <summary>JSON-encoded dependency graph across all quests in the series.</summary>
    public string QuestGraph { get; set; } = "{}";

    public string TargetChain { get; set; } = string.Empty;

    /// <summary>Effective config = shared_config overlaid with per-quest overrides.</summary>
    public Dictionary<string, string> Config { get; set; } = new();

    public DateTime GeneratedDate { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Outcome of running the 5 composition rules from
/// <c>dapp-composition/spec.md §Composition Validation Rules</c>. The manager
/// surfaces this verbatim so the caller can see which rule blocked
/// composition.
/// </summary>
public class CompositionValidationResult
{
    public bool AllQuestsCompleted { get; set; }
    public bool ChainCompleteness { get; set; }
    public bool InputMappingConsistency { get; set; }
    public bool NoCircularDependencies { get; set; }
    public bool HolonBindingsResolved { get; set; }

    /// <summary>Per-rule diagnostic messages; empty when the rule passed.</summary>
    public List<string> Diagnostics { get; set; } = new();

    public bool IsValid =>
        AllQuestsCompleted
        && ChainCompleteness
        && InputMappingConsistency
        && NoCircularDependencies
        && HolonBindingsResolved;
}
