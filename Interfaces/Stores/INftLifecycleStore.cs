using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Immutable NFT requests and compare-and-swap lifecycle transitions.</summary>
public interface INftLifecycleStore
{
    /// <summary>Reserves the current asset version for one durable operation before signing.</summary>
    Task<bool> ReserveAuthorityAsync(NftAssetAuthority authority, NftLifecycle lifecycle, CancellationToken ct = default);
    /// <summary>Cancels only a never-dispatched lifecycle and releases its asset reservation atomically.</summary>
    Task<bool> CancelUnsentAsync(NftLifecycle expected, NftAssetAuthority? authority, CancellationToken ct = default);
    /// <summary>Resolves a credential only through exact network/asset identity and original completed issuance.</summary>
    Task<CredentialOwnershipRecord?> FindCredentialAsync(string chain, string network, string assetId, string? contractAddress, CancellationToken ct = default);
    /// <summary>Reads an immutable participant/creation snapshot page, including terminal rows for stable pagination.</summary>
    Task<IReadOnlyList<NftLifecycle>> ListParticipantSnapshotAsync(Guid participant, DateTimeOffset snapshot, int offset, int limit, CancellationToken ct = default);
    /// <summary>Creates or replays the original request; null means its selected cost receipt cannot admit fresh work.</summary>
    Task<NftLifecycle?> ClaimAsync(NftLifecycle candidate, CancellationToken ct = default);
    Task<NftLifecycle?> GetAsync(string id, CancellationToken ct = default);
    Task<NftLifecycle?> UpdateAsync(NftLifecycle row, long expectedVersion, CancellationToken ct = default, bool requireActionAuthority = false);
    /// <summary>Lists oldest submitted/unknown/confirmed operations eligible for signature-free reconciliation.</summary>
    Task<IReadOnlyList<NftLifecycle>> ListPendingAsync(int limit, CancellationToken ct = default);
    Task<NftAssetAuthority?> GetAuthorityAsync(string nftId, CancellationToken ct = default);
    Task<bool> CommitAsync(NftLifecycle expected, NftAssetAuthority authority, NftOwnership ownership, CancellationToken ct = default);
}
