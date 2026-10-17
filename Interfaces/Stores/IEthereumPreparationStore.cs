using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Durable Ethereum nonce ownership shared by every custody egress path.</summary>
public interface IEthereumPreparationStore
{
    /// <summary>Reads the exact preparation identity.</summary>
    Task<EthereumPreparation?> GetAsync(string id, CancellationToken ct = default);
    /// <summary>Returns the highest fixed-width reserved nonce for this protocol and sender.</summary>
    Task<EthereumPreparation?> GetHighestNonceAsync(string protocol, string sender, CancellationToken ct = default);
    /// <summary>Claims one preparation and nonce, returning null when a different claim owns either.</summary>
    Task<EthereumPreparation?> TryClaimAsync(EthereumPreparation candidate, CancellationToken ct = default);
    /// <summary>Lists released, never-signed reservations for this protocol and sender, lowest nonce first.</summary>
    Task<IReadOnlyList<EthereumPreparation>> ListReleasedAsync(string protocol, string sender, int limit, CancellationToken ct = default);
    /// <summary>Atomically turns one unchanged released reservation into a key-locking tombstone and claims its nonce for the candidate; null when either races.</summary>
    Task<EthereumPreparation?> TryClaimReleasedAsync(EthereumPreparation released, EthereumPreparation candidate, CancellationToken ct = default);
    /// <summary>Changes only recovery state with optimistic version fencing.</summary>
    Task<EthereumPreparation?> UpdateAsync(EthereumPreparation row, long expectedVersion, CancellationToken ct = default);
    /// <summary>Lists one owner's admitted preparations, newest first.</summary>
    Task<IReadOnlyList<EthereumPreparation>> ListOwnerAsync(string owner, int limit, CancellationToken ct = default);
    /// <summary>Lists authorized egress for fair oldest-attempt-first reconciliation.</summary>
    Task<IReadOnlyList<EthereumPreparation>> ListAuthorizedAsync(string network, CancellationToken ct = default);
    /// <summary>Reads the immutable activation checkpoint.</summary>
    Task<EthereumSignerCheckpoint?> GetCheckpointAsync(string id, CancellationToken ct = default);
    /// <summary>Creates the checkpoint without replacing previous activation evidence.</summary>
    Task<EthereumSignerCheckpoint> ClaimCheckpointAsync(EthereumSignerCheckpoint candidate, CancellationToken ct = default);
}
