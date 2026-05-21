using OASIS.WebAPI.Models.Quest;

namespace OASIS.WebAPI.Models.Requests;

public class QuestCreateModel
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<QuestNodeCreateModel> Nodes { get; set; } = new();
    public List<QuestEdgeCreateModel> Edges { get; set; } = new();
}

public class QuestUpdateModel
{
    public string? Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Retained for API back-compat. After the quest-temporal-fork-model
    /// track, runtime status lives on <see cref="QuestRun.Status"/>; setting
    /// this field has no effect on the immutable Quest definition. Validator
    /// still enforces enum validity for clients still sending it.
    /// </summary>
    public QuestStatus? Status { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/quest/runs/{runId}/fork</c>. See ADR §2.3.
/// </summary>
public class QuestForkRequest
{
    public Guid AtNodeId { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Request body for <c>POST /api/quest/runs/{runId}/mark-failed</c>. The
/// supervisor-driven fail path; the audit field
/// <see cref="QuestRun.FailReason"/> distinguishes from internal-error fails.
/// </summary>
public class QuestMarkFailedRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class QuestNodeCreateModel
{
    public string Name { get; set; } = string.Empty;
    public QuestNodeType NodeType { get; set; }
    public string Config { get; set; } = "{}";
    public bool IsEntry { get; set; }
    public bool IsTerminal { get; set; }
    public Guid? NodeTemplateId { get; set; }
}

public class QuestEdgeCreateModel
{
    /// <summary>
    /// Index into the Nodes array of QuestCreateModel.
    /// </summary>
    public int SourceNodeId { get; set; }

    /// <summary>
    /// Index into the Nodes array of QuestCreateModel.
    /// </summary>
    public int TargetNodeId { get; set; }

    public string? Condition { get; set; }
    public QuestEdgeType EdgeType { get; set; } = QuestEdgeType.Control;
}

public class QuestTemplateCreateModel
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<QuestNodeCreateModel> Nodes { get; set; } = new();
    public List<QuestEdgeCreateModel> Edges { get; set; } = new();
    public string Parameters { get; set; } = "{}";
    public string Version { get; set; } = "1.0.0";
    public bool IsPublic { get; set; }
    public List<string> Tags { get; set; } = new();
}

public class QuestNodeTemplateCreateModel
{
    public string Name { get; set; } = string.Empty;
    public QuestNodeType NodeType { get; set; }
    public string? Description { get; set; }
    public string DefaultConfig { get; set; } = "{}";
    public string ConfigSchema { get; set; } = "{}";
    public string InputSchema { get; set; } = "{}";
    public string OutputSchema { get; set; } = "{}";
    public string Version { get; set; } = "1.0.0";
    public bool IsPublic { get; set; }
    public List<string> Tags { get; set; } = new();
}
