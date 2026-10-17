using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface IActionManager
{
    // Action CRUD
    Task<AZOAResult<ActionDefinition>> CreateAsync(ActionCreateModel model, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<ActionDefinition>> GetAsync(Guid id, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<ActionDefinition>>> GetByUserAsync(Guid userId, AZOARequest? request = null);
    /// <summary>Public + published (Active) actions for marketplace discovery/forking.</summary>
    Task<AZOAResult<IEnumerable<ActionDefinition>>> ListPublicAsync(AZOARequest? request = null);
    Task<AZOAResult<ActionDefinition>> UpdateAsync(Guid id, ActionUpdateModel model, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid userId, AZOARequest? request = null);

    // Action definition lifecycle (FR-2, action-dag-semantic-hardening).
    // See Managers/AGENTS.md §publish-lifecycle.
    Task<AZOAResult<ActionDefinition>> PublishAsync(Guid actionId, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<ActionDefinition>> UnpublishAsync(Guid actionId, Guid userId, AZOARequest? request = null);

    // DAG validation
    Task<AZOAResult<bool>> ValidateDAGAsync(Guid actionId, AZOARequest? request = null);
    /// <summary>Owner-scoped DAG validation for the HTTP route; a non-owned action answers like a missing one.</summary>
    Task<AZOAResult<bool>> ValidateDAGAsync(Guid actionId, Guid userId, AZOARequest? request = null);

    // Execution — produces an ActionRun (one execution attempt). Per the
    // action-temporal-fork-model track, runtime state lives on ActionRun +
    // ActionNodeExecution, never on the action definition.
    // tenant-consent-delegation AC4/AC4b: an optional actingTenantId threads the
    // tenant that drove this run (via a tenant-driven child credential) onto the
    // ActionRun so the Tier-2 economic node handlers can stamp it on the produced
    // BlockchainOperation and the custody signing seam's live consent check fires.
    // Null (the default) = user-driven; behaves exactly as before (no regression).
    // Marketplace guards (see Managers/AGENTS.md §action-run-quota + §economic-consent):
    // a per-(user,action) run-start quota rejects treasury/runner-drain re-runs, and a
    // NON-owner run containing value-moving nodes is rejected unless
    // acknowledgeEconomicEffects == true (the runner consented to the disclosed manifest).
    // Both default off/false so existing callers are unchanged; owner runs are exempt from
    // the consent gate and get a higher/unbounded quota ceiling.
    Task<AZOAResult<ActionRun>> ExecuteAsync(Guid actionId, Guid userId, AZOARequest? request = null, Guid? actingTenantId = null, bool acknowledgeEconomicEffects = false);
    Task<AZOAResult<ActionNodeExecution>> ExecuteNodeAsync(Guid actionId, Guid nodeId, Guid userId, AZOARequest? request = null, Guid? actingTenantId = null);

    /// <summary>Pre-run disclosure: the value-moving-node manifest a caller sees BEFORE
    /// committing a marketplace run, so they can consent knowingly. Scoped like a
    /// run-start. See Managers/AGENTS.md §economic-consent.</summary>
    Task<AZOAResult<ActionEconomicManifest>> PreviewRunAsync(Guid actionId, Guid userId, AZOARequest? request = null);

    // Fork — creates a child run branched from `runId` at `atNodeId`. Parent
    // must be Running. See ADR §2.3 for state-machine semantics.
    Task<AZOAResult<ActionRun>> ForkAsync(Guid runId, Guid atNodeId, string reason, Guid userId, AZOARequest? request = null);

    // Supervisor-driven fail path — distinct from the internal-error path
    // by carrying a `FailReason` audit field on the ActionRun. The
    // internal-error path leaves FailReason = null and writes the error
    // onto the failed ActionNodeExecution instead.
    Task<AZOAResult<ActionRun>> MarkRunFailedAsync(Guid runId, string reason, Guid userId, AZOARequest? request = null);

    // Templates
    Task<AZOAResult<ActionTemplate>> CreateTemplateAsync(ActionTemplateCreateModel model, Guid userId, AZOARequest? request = null);
    /// <summary>Owner-or-public read of an action template; an unreadable one answers "not found".</summary>
    Task<AZOAResult<ActionTemplate>> GetTemplateAsync(Guid id, Guid? callerUserId, AZOARequest? request = null);
    /// <summary>Action templates the caller authored or that are public.</summary>
    Task<AZOAResult<IEnumerable<ActionTemplate>>> ListTemplatesAsync(Guid? callerUserId, AZOARequest? request = null);
    Task<AZOAResult<ActionDefinition>> InstantiateTemplateAsync(Guid templateId, Guid userId, Dictionary<string, string>? parameters = null, AZOARequest? request = null);

    // Node Templates
    Task<AZOAResult<ActionNodeTemplate>> CreateNodeTemplateAsync(ActionNodeTemplateCreateModel model, Guid userId, AZOARequest? request = null);
    /// <summary>Node templates the caller authored or that are public.</summary>
    Task<AZOAResult<IEnumerable<ActionNodeTemplate>>> ListNodeTemplatesAsync(Guid? callerUserId, AZOARequest? request = null);

    // ── Action Nodes sub-resource (post-hoc CRUD on a persisted action) ──
    // Mutating the node set re-shapes the DAG; AddNodeAsync defers re-validation
    // to the next ExecuteAsync (or explicit ValidateDAGAsync call). DeleteNodeAsync
    // rejects when the node has any edges referencing it — callers must clear
    // edges first.
    Task<AZOAResult<IEnumerable<ActionNode>>> ListNodesAsync(Guid actionId, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<ActionNode>> AddNodeAsync(Guid actionId, ActionNodeCreateModel model, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<ActionNode>> UpdateNodeAsync(Guid actionId, Guid nodeId, ActionNodeUpdateModel model, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<bool>> DeleteNodeAsync(Guid actionId, Guid nodeId, Guid userId, AZOARequest? request = null);

    // ── Action Edges sub-resource ──
    // AddEdgeAsync runs the DAG validator after mutation and rejects if a
    // cycle would be introduced. GetTopologicalOrderAsync returns node Ids
    // ordered by ActionNode.ExecutionOrder (validator-assigned).
    Task<AZOAResult<ActionEdge>> AddEdgeAsync(Guid actionId, ActionEdgeAddModel model, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<bool>> RemoveEdgeAsync(Guid actionId, Guid edgeId, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<Guid>>> GetTopologicalOrderAsync(Guid actionId, Guid userId, AZOARequest? request = null);

    // ── Action Dependencies sub-resource ──
    // A dependency is satisfied when the referenced action has at least one
    // ActionRun in Succeeded status. CheckDependenciesAsync surfaces the
    // unsatisfied dependency ids without blocking execution.
    Task<AZOAResult<ActionDependency>> AddDependencyAsync(Guid actionId, ActionDependencyCreateModel model, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<bool>> RemoveDependencyAsync(Guid actionId, Guid depId, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<DependencyCheckResult>> CheckDependenciesAsync(Guid actionId, Guid userId, AZOARequest? request = null);

    // ── ActionRun read surface ──
    // Per ADR §2.2, all runtime state lives on ActionRun + ActionNodeExecution.
    // These methods expose the existing runtime to API consumers without
    // re-implementing it on the action definition.
    Task<AZOAResult<ActionRun>> GetRunAsync(Guid runId, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<ActionRun>>> ListRunsByActionAsync(Guid actionId, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<ActionExecutionState>> GetExecutionStateAsync(Guid runId, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<ActionRun>> MarkRunCompletedAsync(Guid runId, Guid userId, AZOARequest? request = null);

    // ── Durable workflow engine (durable-workflow-engine) ──
    // A durable, step-addressable, consumer-driven run. Unlike ExecuteAsync
    // (which runs the whole DAG synchronously in one call), a workflow run maps
    // onto a saga instance: it can SUSPEND between nodes (manual-advance), PARK
    // at a gate/wait node until signalled/timed, survive a process restart, and
    // run a first-class compensation on cancel.

    /// <summary>Start a durable workflow run: create the run + per-node
    /// executions, then enqueue the entry node as the first saga step. Returns
    /// immediately with the run in its initial (Pending/Running) state — the
    /// engine advances it asynchronously.</summary>
    Task<AZOAResult<ActionRun>> StartWorkflowRunAsync(Guid actionId, Guid userId, AZOARequest? request = null, Guid? actingTenantId = null, bool acknowledgeEconomicEffects = false);

    /// <summary>The <c>step(nodeId)</c> primitive: resume a SUSPENDED
    /// manual-advance run from <paramref name="fromNodeId"/> into its successor.
    /// User-scoped. Only Suspended runs accept advance.</summary>
    Task<AZOAResult<ActionRun>> AdvanceAsync(Guid runId, Guid fromNodeId, Guid userId, AZOARequest? request = null);

    /// <summary>Deliver an external signal to a PARKED gate node, un-parking it
    /// so the engine resumes the DAG. User-scoped; idempotent (a duplicate
    /// signal un-parks at most once).</summary>
    Task<AZOAResult<ActionRun>> SignalAsync(Guid runId, string gateId, string? payload, Guid userId, AZOARequest? request = null);

    // ── Reconcile-before-retry re-probe (P7, blockchain-recovery-and-portable-wallets §1.4) ──
    // A run parked in AwaitingReconciliation is NEVER auto-re-broadcast. These
    // entry points re-probe chain truth for the parked chain-action nodes and
    // resume the DAG only on a definite verdict (Confirmed→reconcile-to-success,
    // FailedOnChain→retry/compensation); an indeterminate node stays parked.

    /// <summary>Manually re-probe a single run parked in
    /// <see cref="ActionRunStatus.AwaitingReconciliation"/>. User-scoped; rejects a
    /// run not in that state. NEVER re-broadcasts — it reconciles a Confirmed tx to
    /// success and un-parks, releases a FailedOnChain node to retry/compensation, and
    /// leaves an indeterminate node parked.</summary>
    Task<AZOAResult<ActionReconciliationResult>> ReconcileRunAsync(Guid runId, Guid userId, AZOARequest? request = null);

    /// <summary>Operator sweep: re-probe every run parked in
    /// <see cref="ActionRunStatus.AwaitingReconciliation"/> (unscoped, same chain-truth logic as
    /// <see cref="ReconcileRunAsync"/>). Returns the scanned count with one result per run
    /// re-probed, so zero scanned is never reported as a successful sweep.</summary>
    Task<AZOAResult<ActionReconciliationSweepResult>> SweepReconciliationAsync(AZOARequest? request = null);

    // ── Invitations + access requests (action-invitations-approval) ──
    // Run-authorization (RunAccess/InvitedUserIds) is orthogonal to IsPublic
    // (discoverability). All ops are IDOR-scoped: owner ops via LoadOwnedAction,
    // requester ops by requester identity. See Managers/AGENTS.md §action-invitations.

    /// <summary>Owner sets the run-access mode + optionally seeds the invite list. Owner-only.</summary>
    Task<AZOAResult<ActionDefinition>> SetRunAccessAsync(Guid actionId, Guid ownerUserId, ActionRunAccess runAccess, IEnumerable<Guid>? invitedUserIds = null, AZOARequest? request = null);

    /// <summary>Owner directly adds an invite (idempotent add). Owner-only.</summary>
    Task<AZOAResult<ActionDefinition>> InviteUserAsync(Guid actionId, Guid ownerUserId, Guid targetUserId, AZOARequest? request = null);

    /// <summary>Owner removes an invite (no-op when absent). In-flight runs are unaffected. Owner-only.</summary>
    Task<AZOAResult<ActionDefinition>> RevokeInviteAsync(Guid actionId, Guid ownerUserId, Guid targetUserId, AZOARequest? request = null);

    /// <summary>Any viewer (owner||IsPublic) opens a Pending access request. Idempotent per (action, requester): a live Pending is returned; a prior terminal request does not block a fresh one. Rejects owner/already-invited.</summary>
    Task<AZOAResult<ActionAccessRequest>> RequestAccessAsync(Guid actionId, Guid requesterUserId, string? message = null, AZOARequest? request = null);

    /// <summary>Owner approval queue: all requests for an action, optionally status-filtered. Owner-only.</summary>
    Task<AZOAResult<IEnumerable<ActionAccessRequest>>> ListAccessRequestsAsync(Guid actionId, Guid ownerUserId, ActionAccessRequestStatus? status = null, AZOARequest? request = null);

    /// <summary>Owner approves (appends requester to InvitedUserIds) or rejects a Pending request. Scoped by the request's action owner. Terminal-state transitions rejected.</summary>
    Task<AZOAResult<ActionAccessRequest>> DecideAccessRequestAsync(Guid requestId, Guid ownerUserId, bool approve, string? reason = null, AZOARequest? request = null);

    /// <summary>Requester withdraws their own Pending request. Scoped by requester identity. Terminal-state transitions rejected.</summary>
    Task<AZOAResult<ActionAccessRequest>> WithdrawAccessRequestAsync(Guid requestId, Guid requesterUserId, AZOARequest? request = null);

    /// <summary>Requester's own outbound requests across any action, optionally status-filtered.</summary>
    Task<AZOAResult<IEnumerable<ActionAccessRequest>>> ListMyAccessRequestsAsync(Guid requesterUserId, ActionAccessRequestStatus? status = null, AZOARequest? request = null);
}
