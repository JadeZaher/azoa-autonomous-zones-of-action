using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

public interface IQuestManager
{
    // Quest CRUD
    Task<OASISResult<Quest>> CreateAsync(QuestCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<Quest>> GetAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<Quest>>> GetByAvatarAsync(Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<Quest>> UpdateAsync(Guid id, QuestUpdateModel model, OASISRequest? request = null);
    Task<OASISResult<bool>> DeleteAsync(Guid id, OASISRequest? request = null);

    // DAG validation
    Task<OASISResult<bool>> ValidateDAGAsync(Guid questId, OASISRequest? request = null);

    // Execution — produces a QuestRun (one execution attempt). Per the
    // quest-temporal-fork-model track, runtime state lives on QuestRun +
    // QuestNodeExecution, never on the Quest definition.
    Task<OASISResult<QuestRun>> ExecuteAsync(Guid questId, OASISRequest? request = null);
    Task<OASISResult<QuestNodeExecution>> ExecuteNodeAsync(Guid questId, Guid nodeId, OASISRequest? request = null);

    // Fork — creates a child run branched from `runId` at `atNodeId`. Parent
    // must be Running. See ADR §2.3 for state-machine semantics.
    Task<OASISResult<QuestRun>> ForkAsync(Guid runId, Guid atNodeId, string reason, OASISRequest? request = null);

    // Supervisor-driven fail path — distinct from the internal-error path
    // by carrying a `FailReason` audit field on the QuestRun. The
    // internal-error path leaves FailReason = null and writes the error
    // onto the failed QuestNodeExecution instead.
    Task<OASISResult<QuestRun>> MarkRunFailedAsync(Guid runId, string reason, OASISRequest? request = null);

    // Templates
    Task<OASISResult<QuestTemplate>> CreateTemplateAsync(QuestTemplateCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<QuestTemplate>> GetTemplateAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<QuestTemplate>>> ListTemplatesAsync(OASISRequest? request = null);
    Task<OASISResult<Quest>> InstantiateTemplateAsync(Guid templateId, Guid avatarId, Dictionary<string, string>? parameters = null, OASISRequest? request = null);

    // Node Templates
    Task<OASISResult<QuestNodeTemplate>> CreateNodeTemplateAsync(QuestNodeTemplateCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<QuestNodeTemplate>>> ListNodeTemplatesAsync(OASISRequest? request = null);
}
