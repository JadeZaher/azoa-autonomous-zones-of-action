using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers.Stores;

/// <summary>
/// EF-backed <see cref="IQuestNodeExecutionStore"/> stub. Intentionally NOT
/// implemented for the same reason as <see cref="EfQuestRunStore"/>: the
/// SurrealDB migration deletes the EF/Postgres backplane in wave 3.
/// </summary>
[Obsolete("removed by surrealdb-migration — see conductor/tracks/quest-temporal-fork-model/plan.md task 8")]
public sealed class EfQuestNodeExecutionStore : IQuestNodeExecutionStore
{
    public Task<OASISResult<QuestNodeExecution>> CreateAsync(QuestNodeExecution execution, CancellationToken ct = default)
        => throw new NotImplementedException("EfQuestNodeExecutionStore is a stub; use InMemoryQuestNodeExecutionStore until surrealdb-migration ships the SurrealDB adapter.");

    public Task<OASISResult<QuestNodeExecution>> GetByIdAsync(Guid id, CancellationToken ct = default)
        => throw new NotImplementedException("EfQuestNodeExecutionStore is a stub; use InMemoryQuestNodeExecutionStore until surrealdb-migration ships the SurrealDB adapter.");

    public Task<OASISResult<QuestNodeExecution>> UpdateAsync(
        QuestNodeExecution execution,
        QuestNodeState? expectedState = null,
        CancellationToken ct = default)
        => throw new NotImplementedException("EfQuestNodeExecutionStore is a stub; use InMemoryQuestNodeExecutionStore until surrealdb-migration ships the SurrealDB adapter.");

    public Task<OASISResult<IEnumerable<QuestNodeExecution>>> GetByRunIdAsync(Guid runId, CancellationToken ct = default)
        => throw new NotImplementedException("EfQuestNodeExecutionStore is a stub; use InMemoryQuestNodeExecutionStore until surrealdb-migration ships the SurrealDB adapter.");

    public Task<OASISResult<QuestNodeExecution>> GetByRunAndNodeAsync(Guid runId, Guid nodeId, CancellationToken ct = default)
        => throw new NotImplementedException("EfQuestNodeExecutionStore is a stub; use InMemoryQuestNodeExecutionStore until surrealdb-migration ships the SurrealDB adapter.");

    public Task<OASISResult<QuestNodeExecution?>> TryClaimPendingAsync(Guid runId, Guid nodeId, CancellationToken ct = default)
        => throw new NotImplementedException("EfQuestNodeExecutionStore is a stub; use InMemoryQuestNodeExecutionStore until surrealdb-migration ships the SurrealDB adapter.");
}
