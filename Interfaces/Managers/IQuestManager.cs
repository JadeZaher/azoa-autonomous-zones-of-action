using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

public interface IQuestManager
{
    // Quest CRUD
    Task<OASISResult<Quest>> CreateAsync(QuestCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<Quest>> GetAsync(Guid id, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<Quest>>> GetByAvatarAsync(Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<Quest>> UpdateAsync(Guid id, QuestUpdateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<bool>> DeleteAsync(Guid id, Guid avatarId, OASISRequest? request = null);

    // DAG validation
    Task<OASISResult<bool>> ValidateDAGAsync(Guid questId, OASISRequest? request = null);

    // Execution — produces a QuestRun (one execution attempt). Per the
    // quest-temporal-fork-model track, runtime state lives on QuestRun +
    // QuestNodeExecution, never on the Quest definition.
    Task<OASISResult<QuestRun>> ExecuteAsync(Guid questId, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<QuestNodeExecution>> ExecuteNodeAsync(Guid questId, Guid nodeId, Guid avatarId, OASISRequest? request = null);

    // Fork — creates a child run branched from `runId` at `atNodeId`. Parent
    // must be Running. See ADR §2.3 for state-machine semantics.
    Task<OASISResult<QuestRun>> ForkAsync(Guid runId, Guid atNodeId, string reason, Guid avatarId, OASISRequest? request = null);

    // Supervisor-driven fail path — distinct from the internal-error path
    // by carrying a `FailReason` audit field on the QuestRun. The
    // internal-error path leaves FailReason = null and writes the error
    // onto the failed QuestNodeExecution instead.
    Task<OASISResult<QuestRun>> MarkRunFailedAsync(Guid runId, string reason, Guid avatarId, OASISRequest? request = null);

    // Templates
    Task<OASISResult<QuestTemplate>> CreateTemplateAsync(QuestTemplateCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<QuestTemplate>> GetTemplateAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<QuestTemplate>>> ListTemplatesAsync(OASISRequest? request = null);
    Task<OASISResult<Quest>> InstantiateTemplateAsync(Guid templateId, Guid avatarId, Dictionary<string, string>? parameters = null, OASISRequest? request = null);

    // Node Templates
    Task<OASISResult<QuestNodeTemplate>> CreateNodeTemplateAsync(QuestNodeTemplateCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<QuestNodeTemplate>>> ListNodeTemplatesAsync(OASISRequest? request = null);

    // ── Quest Nodes sub-resource (post-hoc CRUD on a persisted Quest) ──
    // Mutating the node set re-shapes the DAG; AddNodeAsync defers re-validation
    // to the next ExecuteAsync (or explicit ValidateDAGAsync call). DeleteNodeAsync
    // rejects when the node has any edges referencing it — callers must clear
    // edges first.
    Task<OASISResult<IEnumerable<QuestNode>>> ListNodesAsync(Guid questId, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<QuestNode>> AddNodeAsync(Guid questId, QuestNodeCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<QuestNode>> UpdateNodeAsync(Guid questId, Guid nodeId, QuestNodeUpdateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<bool>> DeleteNodeAsync(Guid questId, Guid nodeId, Guid avatarId, OASISRequest? request = null);

    // ── Quest Edges sub-resource ──
    // AddEdgeAsync runs the DAG validator after mutation and rejects if a
    // cycle would be introduced. GetTopologicalOrderAsync returns node Ids
    // ordered by QuestNode.ExecutionOrder (validator-assigned).
    Task<OASISResult<QuestEdge>> AddEdgeAsync(Guid questId, QuestEdgeAddModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<bool>> RemoveEdgeAsync(Guid questId, Guid edgeId, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<Guid>>> GetTopologicalOrderAsync(Guid questId, Guid avatarId, OASISRequest? request = null);

    // ── Quest Dependencies sub-resource ──
    // A dependency is satisfied when the referenced quest has at least one
    // QuestRun in Succeeded status. CheckDependenciesAsync surfaces the
    // unsatisfied dependency ids without blocking execution.
    Task<OASISResult<QuestDependency>> AddDependencyAsync(Guid questId, QuestDependencyCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<bool>> RemoveDependencyAsync(Guid questId, Guid depId, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<DependencyCheckResult>> CheckDependenciesAsync(Guid questId, Guid avatarId, OASISRequest? request = null);

    // ── QuestRun read surface ──
    // Per ADR §2.2, all runtime state lives on QuestRun + QuestNodeExecution.
    // These methods expose the existing runtime to API consumers without
    // re-implementing it on the Quest definition.
    Task<OASISResult<QuestRun>> GetRunAsync(Guid runId, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<QuestRun>>> ListRunsByQuestAsync(Guid questId, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<QuestExecutionState>> GetExecutionStateAsync(Guid runId, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<QuestRun>> MarkRunCompletedAsync(Guid runId, Guid avatarId, OASISRequest? request = null);

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
    Task<OASISResult<QuestRun>> StartWorkflowRunAsync(Guid questId, Guid avatarId, OASISRequest? request = null);

    /// <summary>The <c>step(nodeId)</c> primitive: resume a SUSPENDED
    /// manual-advance run from <paramref name="fromNodeId"/> into its successor.
    /// Avatar-scoped. Only Suspended runs accept advance.</summary>
    Task<OASISResult<QuestRun>> AdvanceAsync(Guid runId, Guid fromNodeId, Guid avatarId, OASISRequest? request = null);

    /// <summary>Deliver an external signal to a PARKED gate node, un-parking it
    /// so the engine resumes the DAG. Avatar-scoped; idempotent (a duplicate
    /// signal un-parks at most once).</summary>
    Task<OASISResult<QuestRun>> SignalAsync(Guid runId, string gateId, string? payload, Guid avatarId, OASISRequest? request = null);
}
