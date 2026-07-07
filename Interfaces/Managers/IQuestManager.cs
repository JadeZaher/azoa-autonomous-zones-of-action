using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface IQuestManager
{
    // Quest CRUD
    Task<AZOAResult<Quest>> CreateAsync(QuestCreateModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<Quest>> GetAsync(Guid id, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<Quest>>> GetByAvatarAsync(Guid avatarId, AZOARequest? request = null);
    /// <summary>Public + published (Active) quests for marketplace discovery/forking.</summary>
    Task<AZOAResult<IEnumerable<Quest>>> ListPublicAsync(AZOARequest? request = null);
    Task<AZOAResult<Quest>> UpdateAsync(Guid id, QuestUpdateModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid avatarId, AZOARequest? request = null);

    // Quest definition lifecycle (FR-2, quest-dag-semantic-hardening).
    // See Managers/AGENTS.md §publish-lifecycle.
    Task<AZOAResult<Quest>> PublishAsync(Guid questId, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<Quest>> UnpublishAsync(Guid questId, Guid avatarId, AZOARequest? request = null);

    // DAG validation
    Task<AZOAResult<bool>> ValidateDAGAsync(Guid questId, AZOARequest? request = null);

    // Execution — produces a QuestRun (one execution attempt). Per the
    // quest-temporal-fork-model track, runtime state lives on QuestRun +
    // QuestNodeExecution, never on the Quest definition.
    // tenant-consent-delegation AC4/AC4b: an optional actingTenantId threads the
    // tenant that drove this run (via a tenant-driven child credential) onto the
    // QuestRun so the Tier-2 economic node handlers can stamp it on the produced
    // BlockchainOperation and the custody signing seam's live consent check fires.
    // Null (the default) = user-driven; behaves exactly as before (no regression).
    // Marketplace guards (see Managers/AGENTS.md §quest-run-quota + §economic-consent):
    // a per-(avatar,quest) run-start quota rejects treasury/runner-drain re-runs, and a
    // NON-owner run containing value-moving nodes is rejected unless
    // acknowledgeEconomicEffects == true (the runner consented to the disclosed manifest).
    // Both default off/false so existing callers are unchanged; owner runs are exempt from
    // the consent gate and get a higher/unbounded quota ceiling.
    Task<AZOAResult<QuestRun>> ExecuteAsync(Guid questId, Guid avatarId, AZOARequest? request = null, Guid? actingTenantId = null, bool acknowledgeEconomicEffects = false);
    Task<AZOAResult<QuestNodeExecution>> ExecuteNodeAsync(Guid questId, Guid nodeId, Guid avatarId, AZOARequest? request = null, Guid? actingTenantId = null);

    /// <summary>Pre-run disclosure: the value-moving-node manifest a caller sees BEFORE
    /// committing a marketplace run, so they can consent knowingly. Scoped like a
    /// run-start. See Managers/AGENTS.md §economic-consent.</summary>
    Task<AZOAResult<QuestEconomicManifest>> PreviewRunAsync(Guid questId, Guid avatarId, AZOARequest? request = null);

    // Fork — creates a child run branched from `runId` at `atNodeId`. Parent
    // must be Running. See ADR §2.3 for state-machine semantics.
    Task<AZOAResult<QuestRun>> ForkAsync(Guid runId, Guid atNodeId, string reason, Guid avatarId, AZOARequest? request = null);

    // Supervisor-driven fail path — distinct from the internal-error path
    // by carrying a `FailReason` audit field on the QuestRun. The
    // internal-error path leaves FailReason = null and writes the error
    // onto the failed QuestNodeExecution instead.
    Task<AZOAResult<QuestRun>> MarkRunFailedAsync(Guid runId, string reason, Guid avatarId, AZOARequest? request = null);

    // Templates
    Task<AZOAResult<QuestTemplate>> CreateTemplateAsync(QuestTemplateCreateModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<QuestTemplate>> GetTemplateAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<QuestTemplate>>> ListTemplatesAsync(AZOARequest? request = null);
    Task<AZOAResult<Quest>> InstantiateTemplateAsync(Guid templateId, Guid avatarId, Dictionary<string, string>? parameters = null, AZOARequest? request = null);

    // Node Templates
    Task<AZOAResult<QuestNodeTemplate>> CreateNodeTemplateAsync(QuestNodeTemplateCreateModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<QuestNodeTemplate>>> ListNodeTemplatesAsync(AZOARequest? request = null);

    // ── Quest Nodes sub-resource (post-hoc CRUD on a persisted Quest) ──
    // Mutating the node set re-shapes the DAG; AddNodeAsync defers re-validation
    // to the next ExecuteAsync (or explicit ValidateDAGAsync call). DeleteNodeAsync
    // rejects when the node has any edges referencing it — callers must clear
    // edges first.
    Task<AZOAResult<IEnumerable<QuestNode>>> ListNodesAsync(Guid questId, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<QuestNode>> AddNodeAsync(Guid questId, QuestNodeCreateModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<QuestNode>> UpdateNodeAsync(Guid questId, Guid nodeId, QuestNodeUpdateModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<bool>> DeleteNodeAsync(Guid questId, Guid nodeId, Guid avatarId, AZOARequest? request = null);

    // ── Quest Edges sub-resource ──
    // AddEdgeAsync runs the DAG validator after mutation and rejects if a
    // cycle would be introduced. GetTopologicalOrderAsync returns node Ids
    // ordered by QuestNode.ExecutionOrder (validator-assigned).
    Task<AZOAResult<QuestEdge>> AddEdgeAsync(Guid questId, QuestEdgeAddModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<bool>> RemoveEdgeAsync(Guid questId, Guid edgeId, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<Guid>>> GetTopologicalOrderAsync(Guid questId, Guid avatarId, AZOARequest? request = null);

    // ── Quest Dependencies sub-resource ──
    // A dependency is satisfied when the referenced quest has at least one
    // QuestRun in Succeeded status. CheckDependenciesAsync surfaces the
    // unsatisfied dependency ids without blocking execution.
    Task<AZOAResult<QuestDependency>> AddDependencyAsync(Guid questId, QuestDependencyCreateModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<bool>> RemoveDependencyAsync(Guid questId, Guid depId, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<DependencyCheckResult>> CheckDependenciesAsync(Guid questId, Guid avatarId, AZOARequest? request = null);

    // ── QuestRun read surface ──
    // Per ADR §2.2, all runtime state lives on QuestRun + QuestNodeExecution.
    // These methods expose the existing runtime to API consumers without
    // re-implementing it on the Quest definition.
    Task<AZOAResult<QuestRun>> GetRunAsync(Guid runId, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<QuestRun>>> ListRunsByQuestAsync(Guid questId, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<QuestExecutionState>> GetExecutionStateAsync(Guid runId, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<QuestRun>> MarkRunCompletedAsync(Guid runId, Guid avatarId, AZOARequest? request = null);

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
    Task<AZOAResult<QuestRun>> StartWorkflowRunAsync(Guid questId, Guid avatarId, AZOARequest? request = null, Guid? actingTenantId = null, bool acknowledgeEconomicEffects = false);

    /// <summary>The <c>step(nodeId)</c> primitive: resume a SUSPENDED
    /// manual-advance run from <paramref name="fromNodeId"/> into its successor.
    /// Avatar-scoped. Only Suspended runs accept advance.</summary>
    Task<AZOAResult<QuestRun>> AdvanceAsync(Guid runId, Guid fromNodeId, Guid avatarId, AZOARequest? request = null);

    /// <summary>Deliver an external signal to a PARKED gate node, un-parking it
    /// so the engine resumes the DAG. Avatar-scoped; idempotent (a duplicate
    /// signal un-parks at most once).</summary>
    Task<AZOAResult<QuestRun>> SignalAsync(Guid runId, string gateId, string? payload, Guid avatarId, AZOARequest? request = null);

    // ── Reconcile-before-retry re-probe (P7, blockchain-recovery-and-portable-wallets §1.4) ──
    // A run parked in AwaitingReconciliation is NEVER auto-re-broadcast. These
    // entry points re-probe chain truth for the parked chain-action nodes and
    // resume the DAG only on a definite verdict (Confirmed→reconcile-to-success,
    // FailedOnChain→retry/compensation); an indeterminate node stays parked.

    /// <summary>Manually re-probe a single run parked in
    /// <see cref="QuestRunStatus.AwaitingReconciliation"/>. Avatar-scoped; rejects a
    /// run not in that state. NEVER re-broadcasts — it reconciles a Confirmed tx to
    /// success and un-parks, releases a FailedOnChain node to retry/compensation, and
    /// leaves an indeterminate node parked.</summary>
    Task<AZOAResult<QuestReconciliationResult>> ReconcileRunAsync(Guid runId, Guid avatarId, AZOARequest? request = null);

    /// <summary>Operator/background sweep: re-probe EVERY run currently parked in
    /// <see cref="QuestRunStatus.AwaitingReconciliation"/>. Unscoped (operator
    /// context) — the per-run re-probe is the same chain-truth logic as
    /// <see cref="ReconcileRunAsync"/>. Returns one result per run swept.</summary>
    Task<AZOAResult<IEnumerable<QuestReconciliationResult>>> SweepReconciliationAsync(AZOARequest? request = null);

    // ── Invitations + access requests (quest-invitations-approval) ──
    // Run-authorization (RunAccess/InvitedAvatarIds) is orthogonal to IsPublic
    // (discoverability). All ops are IDOR-scoped: owner ops via LoadOwnedQuest,
    // requester ops by requester identity. See Managers/AGENTS.md §quest-invitations.

    /// <summary>Owner sets the run-access mode + optionally seeds the invite list. Owner-only.</summary>
    Task<AZOAResult<Quest>> SetRunAccessAsync(Guid questId, Guid ownerAvatarId, QuestRunAccess runAccess, IEnumerable<Guid>? invitedAvatarIds = null, AZOARequest? request = null);

    /// <summary>Owner directly adds an invite (idempotent add). Owner-only.</summary>
    Task<AZOAResult<Quest>> InviteAvatarAsync(Guid questId, Guid ownerAvatarId, Guid targetAvatarId, AZOARequest? request = null);

    /// <summary>Owner removes an invite (no-op when absent). In-flight runs are unaffected. Owner-only.</summary>
    Task<AZOAResult<Quest>> RevokeInviteAsync(Guid questId, Guid ownerAvatarId, Guid targetAvatarId, AZOARequest? request = null);

    /// <summary>Any viewer (owner||IsPublic) opens a Pending access request. Idempotent per (quest, requester): a live Pending is returned; a prior terminal request does not block a fresh one. Rejects owner/already-invited.</summary>
    Task<AZOAResult<QuestAccessRequest>> RequestAccessAsync(Guid questId, Guid requesterAvatarId, string? message = null, AZOARequest? request = null);

    /// <summary>Owner approval queue: all requests for a quest, optionally status-filtered. Owner-only.</summary>
    Task<AZOAResult<IEnumerable<QuestAccessRequest>>> ListAccessRequestsAsync(Guid questId, Guid ownerAvatarId, QuestAccessRequestStatus? status = null, AZOARequest? request = null);

    /// <summary>Owner approves (appends requester to InvitedAvatarIds) or rejects a Pending request. Scoped by the request's quest owner. Terminal-state transitions rejected.</summary>
    Task<AZOAResult<QuestAccessRequest>> DecideAccessRequestAsync(Guid requestId, Guid ownerAvatarId, bool approve, string? reason = null, AZOARequest? request = null);

    /// <summary>Requester withdraws their own Pending request. Scoped by requester identity. Terminal-state transitions rejected.</summary>
    Task<AZOAResult<QuestAccessRequest>> WithdrawAccessRequestAsync(Guid requestId, Guid requesterAvatarId, AZOARequest? request = null);

    /// <summary>Requester's own outbound requests across any quest, optionally status-filtered.</summary>
    Task<AZOAResult<IEnumerable<QuestAccessRequest>>> ListMyAccessRequestsAsync(Guid requesterAvatarId, QuestAccessRequestStatus? status = null, AZOARequest? request = null);
}
