namespace AZOA.WebAPI.Models.Actions;

/// <summary>
/// A single executable DAG representing a workflow unit.
/// Each ActionDefinition is a directed acyclic graph with entry/terminal nodes,
/// optional dependencies on completed actions, and reusable node templates.
/// </summary>
/// <remarks>
/// Runtime state (status, completion timestamp, per-node outputs) lives on
/// <see cref="ActionRun"/> and <see cref="ActionNodeExecution"/> after the
/// <c>action-temporal-fork-model</c> track. The ActionDefinition definition itself is
/// shape-only and immutable in-flight.
/// </remarks>
public class ActionDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid UserId { get; set; }

    public List<ActionNode> Nodes { get; set; } = new();
    public List<ActionEdge> Edges { get; set; } = new();
    public List<ActionDependency> Dependencies { get; set; } = new();

    public Guid? TemplateId { get; set; }
    public Guid? DappSeriesId { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Definition birthdate. STAYS on the definition (not a runtime artifact).</summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Definition lifecycle: Draft (editable) or Active (locked for execution).
    /// Reintroduced by action-dag-semantic-hardening FR-2; see Managers/AGENTS.md §publish-lifecycle.
    /// </summary>
    public ActionStatus Status { get; set; } = ActionStatus.Draft;

    /// <summary>
    /// Optimistic-concurrency token for the definition lifecycle (F6 TOCTOU guard).
    /// Bumped on every CAS status transition; see Managers/AGENTS.md §publish-lifecycle.
    /// </summary>
    public long Version { get; set; }

    /// <summary>Marketplace visibility. Private (owner-only) by default; published actions can be started by non-owners.</summary>
    public bool IsPublic { get; set; }

    /// <summary>Origin/creator user when this action is a marketplace copy; null for first-party actions.</summary>
    public Guid? OriginUserId { get; set; }

    /// <summary>Content hash of the node/edge graph as of the last publish (bait-and-switch guard). Null while never published; recomputed on every PublishAsync. See Managers/AGENTS.md §published-version-hash.</summary>
    public string? PublishedVersionHash { get; set; }

    /// <summary>Run-authorization dimension, orthogonal to IsPublic (discoverability). Open = anyone who can view may run; InviteOnly = owner + InvitedUserIds only. See Persistence/SurrealDb/Models/AGENTS.md §action-access-request.</summary>
    public ActionRunAccess RunAccess { get; set; } = ActionRunAccess.Open;

    /// <summary>Approved invite set for InviteOnly actions; owner is always implicitly invited.</summary>
    public List<Guid> InvitedUserIds { get; set; } = new();
}
