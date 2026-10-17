using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Stores bounded readonly proof-page progress independently of signing and first-send authority.</summary>
public interface IWttEvidenceProgressStore
{
    /// <summary>Returns the winning initial cursor for an immutable provider query and boundary.</summary>
    Task<WttEvidenceProgress> ClaimAsync(string bindingJson, string initialStateJson, CancellationToken ct = default);
    /// <summary>Advances only the exact prior page, rejecting repeated cursors and preserving its evidence digest.</summary>
    Task<WttEvidenceProgress?> AdvanceAsync(WttEvidenceProgress expected, string stateJson, string pageDigest,
        string? nextCursor, CancellationToken ct = default);
}
