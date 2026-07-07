namespace AZOA.WebAPI.Models.Quest;

/// <summary>
/// Pre-run disclosure of the value-moving nodes in a quest a NON-owner is about to
/// run against themselves (marketplace consent surface). See Services/Quest/AGENTS.md
/// §economic-manifest.
/// </summary>
public class QuestEconomicManifest
{
    /// <summary>Quest whose graph this manifest describes.</summary>
    public Guid QuestId { get; set; }

    /// <summary>The published-version hash the manifest was computed against (binds disclosure to the exact graph revision).</summary>
    public string? PublishedVersionHash { get; set; }

    /// <summary>True when at least one value-moving node is present — the run-start consent gate applies.</summary>
    public bool HasEconomicNodes => Entries.Count > 0;

    /// <summary>One entry per value-moving node, in the order the runner would encounter them.</summary>
    public List<QuestEconomicManifestEntry> Entries { get; set; } = new();
}

/// <summary>One value-moving node's declared effect, for pre-run disclosure.</summary>
public class QuestEconomicManifestEntry
{
    public Guid NodeId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public QuestNodeType NodeType { get; set; }

    /// <summary>Declared destination (recipient address / target chain), best-effort from the node config. Null when the node config carries none.</summary>
    public string? Destination { get; set; }

    /// <summary>Declared amount/total, best-effort from the node config. Null when the node config carries none.</summary>
    public string? Amount { get; set; }
}
