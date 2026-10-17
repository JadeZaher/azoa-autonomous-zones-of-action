using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Preserves one encrypted stage seed across retries, concurrency and encryption-key rotation.</summary>
public interface IWttStageKeyStore
{
    Task<WttStageKeyEnvelope?> GetAsync(string id, CancellationToken ct = default);
    /// <summary>Returns the winning envelope for this immutable binding; duplicate candidates never replace it.</summary>
    Task<WttStageKeyEnvelope> ClaimAsync(WttStageKeyEnvelope candidate, CancellationToken ct = default);
    /// <summary>Changes encryption wrapping only if the exact prior identity, envelope and version still match.</summary>
    Task<bool> TryRewrapAsync(WttStageKeyEnvelope expected, string newKeyVersion, string newEnvelopeJson, CancellationToken ct = default);
    /// <summary>Finds one retained envelope unreadable by the proposed keyring; any result prevents retirement/readiness.</summary>
    Task<WttStageKeyEnvelope?> FindUnavailableKeyAsync(IReadOnlyCollection<string> availableVersions, CancellationToken ct = default);
}
