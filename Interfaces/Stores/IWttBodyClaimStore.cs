using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Owns canonical-body reservation without implying submission, redemption or settlement.</summary>
public interface IWttBodyClaimStore
{
    Task<WttBodyClaim?> GetAsync(string id, CancellationToken ct = default);
    /// <summary>Returns the exact immutable owner on admission/replay, or null on a conflicting body, publication or operation.</summary>
    Task<WttBodyClaim?> ClaimAsync(WttBodyClaim candidate, CancellationToken ct = default);
}
