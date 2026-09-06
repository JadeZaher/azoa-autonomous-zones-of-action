using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Models.Requests;

public class ActionCreateModel
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<ActionNodeCreateModel> Nodes { get; set; } = new();
    public List<ActionEdgeCreateModel> Edges { get; set; } = new();

    /// <summary>Marketplace visibility. Defaults to false (private, owner-only). When true, non-owners can start this action under their own context once it is published (Active).</summary>
    public bool IsPublic { get; set; }
}

public class ActionUpdateModel
{
    public string? Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Retained for API back-compat. After the action-temporal-fork-model
    /// track, runtime status lives on <see cref="ActionRun.Status"/>; setting
    /// this field has no effect on the immutable ActionDefinition definition. Validator
    /// still enforces enum validity for clients still sending it.
    /// </summary>
    public ActionStatus? Status { get; set; }

    /// <summary>Marketplace visibility toggle. Null = leave unchanged; owner-only (IDOR-safe like the rest of the update surface).</summary>
    public bool? IsPublic { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/action/runs/{runId}/fork</c>. See ADR §2.3.
/// </summary>
public class ActionForkRequest
{
    public Guid AtNodeId { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Request body for <c>POST /api/action/runs/{runId}/mark-failed</c>. The
/// supervisor-driven fail path; the audit field
/// <see cref="ActionRun.FailReason"/> distinguishes from internal-error fails.
/// </summary>
public class ActionMarkFailedRequest
{
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Body for the durable-workflow-engine <c>advance</c> endpoint (the
/// <c>step(nodeId)</c> primitive): resume a Suspended manual-advance run from
/// <see cref="FromNodeId"/> into its successor.
/// </summary>
public class ActionAdvanceRequest
{
    public Guid FromNodeId { get; set; }
}

/// <summary>
/// Body for the durable-workflow-engine <c>signal</c> endpoint: deliver an
/// external signal to a parked gate node, un-parking it.
/// </summary>
public class ActionSignalRequest
{
    /// <summary>The gate id the parked node is waiting on.</summary>
    public string GateId { get; set; } = string.Empty;

    /// <summary>Optional signal body carried into the resumed gate node
    /// (e.g. "phase-met" / "cancelled"). The economic-primitive-nodes track's
    /// GateCheck handler interprets it; this engine only delivers it.</summary>
    public string? Payload { get; set; }
}

public class ActionNodeCreateModel
{
    public string Name { get; set; } = string.Empty;
    public ActionNodeType NodeType { get; set; }
    public string Config { get; set; } = "{}";
    public bool IsEntry { get; set; }
    public bool IsTerminal { get; set; }
    public Guid? NodeTemplateId { get; set; }
}

public class ActionEdgeCreateModel
{
    /// <summary>
    /// Index into the Nodes array of ActionCreateModel.
    /// </summary>
    public int SourceNodeId { get; set; }

    /// <summary>
    /// Index into the Nodes array of ActionCreateModel.
    /// </summary>
    public int TargetNodeId { get; set; }

    public string? Condition { get; set; }
    public ActionEdgeType EdgeType { get; set; } = ActionEdgeType.Control;
}

// ─── Sub-resource mutation DTOs (post-hoc edits on a persisted ActionDefinition) ────────
// These differ from the inner-Create models above by using concrete Guid
// references instead of array indices. The inner-Create models are kept
// untouched so ActionManager.CreateAsync continues to compile.

/// <summary>
/// Request body for <c>PUT /api/action/{actionId}/nodes/{nodeId}</c>. Each
/// field is nullable so callers can patch a subset; the manager applies only
/// the non-null fields. Mutating <see cref="NodeType"/> or the underlying
/// Config is reserved for a future schema-aware patch surface — at this stage
/// only the cheap-to-edit shape fields are exposed.
/// </summary>
public class ActionNodeUpdateModel
{
    public string? Name { get; set; }
    public string? Config { get; set; }
    public bool? IsEntry { get; set; }
    public bool? IsTerminal { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/action/{actionId}/edges</c>. Unlike the inner
/// <see cref="ActionEdgeCreateModel"/> which uses array indices into the
/// ActionCreateModel.Nodes payload, this variant carries concrete Guid
/// references because the parent ActionDefinition is already persisted.
/// </summary>
public class ActionEdgeAddModel
{
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
    public string? Condition { get; set; }
    public AZOA.WebAPI.Models.Actions.ActionEdgeType EdgeType { get; set; } = AZOA.WebAPI.Models.Actions.ActionEdgeType.Control;
}

/// <summary>
/// Request body for <c>POST /api/action/{actionId}/dependencies</c>. A cross-action
/// edge expressing that this action depends on the completion of another action.
/// </summary>
public class ActionDependencyCreateModel
{
    public Guid DependsOnActionId { get; set; }

    /// <summary>
    /// Optional: depend on a specific node output rather than full action completion.
    /// Mirrors <see cref="AZOA.WebAPI.Models.Actions.ActionDependency.DependsOnNodeId"/>.
    /// </summary>
    public Guid? DependsOnNodeId { get; set; }

    public AZOA.WebAPI.Models.Actions.ActionDependencyType DependencyType { get; set; }
        = AZOA.WebAPI.Models.Actions.ActionDependencyType.Required;

    /// <summary>Optional audit description; ignored by the manager today, reserved for future use.</summary>
    public string? Description { get; set; }
}

public class ActionTemplateCreateModel
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<ActionNodeCreateModel> Nodes { get; set; } = new();
    public List<ActionEdgeCreateModel> Edges { get; set; } = new();
    public string Parameters { get; set; } = "{}";
    public string Version { get; set; } = "1.0.0";
    public bool IsPublic { get; set; }
    public List<string> Tags { get; set; } = new();
}

public class ActionNodeTemplateCreateModel
{
    public string Name { get; set; } = string.Empty;
    public ActionNodeType NodeType { get; set; }
    public string? Description { get; set; }
    public string DefaultConfig { get; set; } = "{}";
    public string ConfigSchema { get; set; } = "{}";
    public string InputSchema { get; set; } = "{}";
    public string OutputSchema { get; set; } = "{}";
    public string Version { get; set; } = "1.0.0";
    public bool IsPublic { get; set; }
    public List<string> Tags { get; set; } = new();
}
