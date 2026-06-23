using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Models.Requests;

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

/// <summary>
/// Body for the durable-workflow-engine <c>advance</c> endpoint (the
/// <c>step(nodeId)</c> primitive): resume a Suspended manual-advance run from
/// <see cref="FromNodeId"/> into its successor.
/// </summary>
public class QuestAdvanceRequest
{
    public Guid FromNodeId { get; set; }
}

/// <summary>
/// Body for the durable-workflow-engine <c>signal</c> endpoint: deliver an
/// external signal to a parked gate node, un-parking it.
/// </summary>
public class QuestSignalRequest
{
    /// <summary>The gate id the parked node is waiting on.</summary>
    public string GateId { get; set; } = string.Empty;

    /// <summary>Optional signal body carried into the resumed gate node
    /// (e.g. "phase-met" / "cancelled"). The economic-primitive-nodes track's
    /// GateCheck handler interprets it; this engine only delivers it.</summary>
    public string? Payload { get; set; }
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

// ─── Sub-resource mutation DTOs (post-hoc edits on a persisted Quest) ────────
// These differ from the inner-Create models above by using concrete Guid
// references instead of array indices. The inner-Create models are kept
// untouched so QuestManager.CreateAsync continues to compile.

/// <summary>
/// Request body for <c>PUT /api/quest/{questId}/nodes/{nodeId}</c>. Each
/// field is nullable so callers can patch a subset; the manager applies only
/// the non-null fields. Mutating <see cref="NodeType"/> or the underlying
/// Config is reserved for a future schema-aware patch surface — at this stage
/// only the cheap-to-edit shape fields are exposed.
/// </summary>
public class QuestNodeUpdateModel
{
    public string? Name { get; set; }
    public string? Config { get; set; }
    public bool? IsEntry { get; set; }
    public bool? IsTerminal { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/quest/{questId}/edges</c>. Unlike the inner
/// <see cref="QuestEdgeCreateModel"/> which uses array indices into the
/// QuestCreateModel.Nodes payload, this variant carries concrete Guid
/// references because the parent Quest is already persisted.
/// </summary>
public class QuestEdgeAddModel
{
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
    public string? Condition { get; set; }
    public AZOA.WebAPI.Models.Quest.QuestEdgeType EdgeType { get; set; } = AZOA.WebAPI.Models.Quest.QuestEdgeType.Control;
}

/// <summary>
/// Request body for <c>POST /api/quest/{questId}/dependencies</c>. A cross-quest
/// edge expressing that this quest depends on the completion of another quest.
/// </summary>
public class QuestDependencyCreateModel
{
    public Guid DependsOnQuestId { get; set; }

    /// <summary>
    /// Optional: depend on a specific node output rather than full quest completion.
    /// Mirrors <see cref="AZOA.WebAPI.Models.Quest.QuestDependency.DependsOnNodeId"/>.
    /// </summary>
    public Guid? DependsOnNodeId { get; set; }

    public AZOA.WebAPI.Models.Quest.QuestDependencyType DependencyType { get; set; }
        = AZOA.WebAPI.Models.Quest.QuestDependencyType.Required;

    /// <summary>Optional audit description; ignored by the manager today, reserved for future use.</summary>
    public string? Description { get; set; }
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
