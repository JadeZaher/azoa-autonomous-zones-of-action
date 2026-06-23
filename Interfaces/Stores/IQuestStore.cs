using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Persistence boundary for <see cref="Quest"/>, <see cref="QuestTemplate"/> and <see cref="QuestNodeTemplate"/>.</summary>
public interface IQuestStore
{
    /// <summary>Loads a single quest by id.</summary>
    Task<AZOAResult<Quest>> GetQuestAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads all quests owned by an avatar.</summary>
    Task<AZOAResult<IEnumerable<Quest>>> GetQuestsByAvatarAsync(Guid avatarId, CancellationToken ct = default);

    /// <summary>Loads all quests belonging to a dapp series.</summary>
    Task<AZOAResult<IEnumerable<Quest>>> GetQuestsByDappSeriesAsync(Guid dappSeriesId, CancellationToken ct = default);

    /// <summary>Inserts or updates a quest (including its node/edge graph).</summary>
    Task<AZOAResult<Quest>> UpsertQuestAsync(Quest quest, CancellationToken ct = default);

    /// <summary>Deletes a quest by id.</summary>
    Task<AZOAResult<bool>> DeleteQuestAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads a single quest template by id.</summary>
    Task<AZOAResult<QuestTemplate>> GetQuestTemplateAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads every quest template.</summary>
    Task<AZOAResult<IEnumerable<QuestTemplate>>> GetAllQuestTemplatesAsync(CancellationToken ct = default);

    /// <summary>Inserts or updates a quest template.</summary>
    Task<AZOAResult<QuestTemplate>> UpsertQuestTemplateAsync(QuestTemplate template, CancellationToken ct = default);

    /// <summary>Deletes a quest template by id.</summary>
    Task<AZOAResult<bool>> DeleteQuestTemplateAsync(Guid id, CancellationToken ct = default);

    /// <summary>Inserts or updates a quest node template.</summary>
    Task<AZOAResult<QuestNodeTemplate>> UpsertQuestNodeTemplateAsync(QuestNodeTemplate template, CancellationToken ct = default);

    /// <summary>Loads every quest node template.</summary>
    Task<AZOAResult<IEnumerable<QuestNodeTemplate>>> GetAllQuestNodeTemplatesAsync(CancellationToken ct = default);
}
