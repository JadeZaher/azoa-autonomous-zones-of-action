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

    /// <summary>Loads all quests the owner opted into the public marketplace (IsPublic=true). Status filtered by the caller.</summary>
    Task<AZOAResult<IEnumerable<Quest>>> GetPublicQuestsAsync(CancellationToken ct = default);

    /// <summary>Loads all quests belonging to a dapp series.</summary>
    Task<AZOAResult<IEnumerable<Quest>>> GetQuestsByDappSeriesAsync(Guid dappSeriesId, CancellationToken ct = default);

    /// <summary>Inserts or updates a quest (including its node/edge graph).</summary>
    Task<AZOAResult<Quest>> UpsertQuestAsync(Quest quest, CancellationToken ct = default);

    /// <summary>Deletes a quest by id.</summary>
    Task<AZOAResult<bool>> DeleteQuestAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Compare-and-swap the definition lifecycle Status (F6 TOCTOU guard).
    /// Issues a single conditional UPDATE that flips <paramref name="expected"/> →
    /// <paramref name="next"/> and increments <c>version</c> ONLY when the row is
    /// still at (<paramref name="expected"/>, <paramref name="expectedVersion"/>).
    /// Returns the affected-row count VERBATIM (0 = lost the race / stale version;
    /// 1 = won). The store never asserts==1, retries, or read-modify-writes.
    /// Mirrors <c>IBridgeStore.TryTransitionBridgeStatusAsync</c>.
    /// </summary>
    Task<int> TryTransitionQuestStatusAsync(Guid id, QuestStatus expected, QuestStatus next, long expectedVersion, CancellationToken ct = default);

    /// <summary>
    /// Confirms the quest is STILL at (<paramref name="expected"/>,
    /// <paramref name="expectedVersion"/>) via a conditional no-op self-write,
    /// without changing status or version. Returns the affected-row count VERBATIM
    /// (1 = definition unchanged since the caller read it; 0 = it moved — e.g. an
    /// unpublish raced this run-start). Closes the unpublish-vs-run-start TOCTOU.
    /// </summary>
    Task<int> TryConfirmQuestStateAsync(Guid id, QuestStatus expected, long expectedVersion, CancellationToken ct = default);

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
