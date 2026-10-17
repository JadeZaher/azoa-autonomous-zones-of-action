using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Operator-only person-operation claims; see Providers/Stores/Surreal/AGENTS.md.</summary>
public interface IPersonOperationClaimStore
{
    /// <summary>Atomically acquires or updates an eligible claim, otherwise returns its incumbent; the first decision cutoff is immutable.</summary>
    Task<AZOAResult<PersonOperationClaim>> TryClaimAsync(PersonOperationClaim candidate, CancellationToken ct = default);

    /// <summary>Lists live claims in an authorized operation domain without providing a consumer person-key lookup.</summary>
    Task<AZOAResult<IReadOnlyList<PersonOperationClaim>>> ListLiveByOperationAsync(string uniquenessScope, string operationKey, CancellationToken ct = default);

    /// <summary>Reads the current claim by its immutable domain identity.</summary>
    Task<AZOAResult<PersonOperationClaim?>> GetAsync(string uniquenessScope, string operationKey, string personKey, CancellationToken ct = default);

    /// <summary>Releases the current holder before database cutoff; missing, wrong-owner or already-released claims return false.</summary>
    Task<AZOAResult<bool>> ReleaseAsync(string uniquenessScope, string operationKey, string personKey, string userId,
        DateTimeOffset releasedAt, CancellationToken ct = default);
}
