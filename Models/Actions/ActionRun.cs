namespace AZOA.WebAPI.Models.Actions;

/// <summary>
/// One execution attempt of a <see cref="ActionDefinition"/> definition. Carries runtime
/// state that was previously mutated in-place on <see cref="ActionDefinition"/> itself,
/// which made re-runs and forks impossible (see
/// <c>conductor/tracks/action-temporal-fork-model/ADR.md</c> §1).
/// </summary>
/// <remarks>
/// <para>
/// Lineage forms a <b>tree</b> via <see cref="ParentRunId"/>:
/// re-running a Succeeded action creates a new <i>root</i> run
/// (<see cref="ParentRunId"/> = null); forking a Running run creates a child
/// run with <see cref="ParentRunId"/> set to the parent and
/// <see cref="ForkedAtNodeId"/> set to the fork point.
/// </para>
/// <para>
/// Per-node state lives in <see cref="ActionNodeExecution"/>, never on
/// <see cref="ActionNode"/>.
/// </para>
/// </remarks>
public class ActionRun
{
    /// <summary>Run identity. Unique across all runs of all actions.</summary>
    public Guid Id { get; set; }

    /// <summary>Action definition this run executes.</summary>
    public Guid ActionId { get; set; }

    /// <summary>User that initiated this run (denormalized for query convenience).</summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The tenant that DROVE this run via a tenant-driven child credential, or
    /// null when the run is user-driven (tenant-consent-delegation AC4/AC4b).
    /// Persisted on the run — NOT ambient — so it survives the async saga-worker
    /// hop and reaches the durable Tier-2 economic node handlers, which stamp it
    /// onto the produced <c>BlockchainOperation</c> so the custody signing seam
    /// can run its live consent gate (fail-closed without a grant). A tenant-driven
    /// run stays tenant-driven across forks/children (inherited from the parent
    /// run), mirroring the synchronous Allocation/FungibleToken value paths.
    /// </summary>
    public Guid? ActingTenantId { get; set; }

    /// <summary>Current lifecycle position. See <see cref="ActionRunStatus"/>.</summary>
    public ActionRunStatus Status { get; set; } = ActionRunStatus.Pending;

    /// <summary>Wall-clock time at which the run row was created.</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Wall-clock time at which the run reached a terminal state. Null while non-terminal.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>
    /// Parent run id if this run was forked from another. Null for root runs
    /// (initial executions and re-runs). Lineage is a tree, not a DAG.
    /// </summary>
    public Guid? ParentRunId { get; set; }

    /// <summary>
    /// Node at which the fork occurred. Set iff this is a child fork run.
    /// Nodes with <c>ExecutionOrder &lt; forkPoint.ExecutionOrder</c> are
    /// inherited by reference from the parent's <see cref="ActionNodeExecution"/>
    /// rows (no recompute, no duplication).
    /// </summary>
    public Guid? ForkedAtNodeId { get; set; }

    /// <summary>Free-form audit reason supplied when the fork was triggered.</summary>
    public string? ForkReason { get; set; }

    /// <summary>
    /// Free-form audit reason when a supervisor explicitly marked the run failed
    /// (distinct from internal error paths that set <see cref="Status"/> to
    /// <see cref="ActionRunStatus.Failed"/> via node failure).
    /// </summary>
    public string? FailReason { get; set; }

    /// <summary>Marketplace provenance: the origin action this run was started from when the runner is not the action owner. Null for owner runs.</summary>
    public Guid? SourceActionId { get; set; }

    /// <summary>Marketplace provenance: the origin action's owner/creator user when the runner is not the owner. Null for owner runs.</summary>
    public Guid? OriginUserId { get; set; }

    /// <summary>
    /// The action's <see cref="ActionDefinition.PublishedVersionHash"/> at the instant this run
    /// STARTED — binds the run to the exact graph revision the runner saw/consented to.
    /// A later unpublish→edit→republish changes the action's hash, so a run's stamp no
    /// longer matching the live action is detectable evidence of a bait-and-switch.
    /// See Managers/AGENTS.md §published-version-hash.
    /// </summary>
    public string? PublishedVersionHash { get; set; }
}
