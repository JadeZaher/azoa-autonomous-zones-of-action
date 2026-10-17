using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Fail-closed: no External wallet is proven (tests and hosts without the ownership-proof store).</summary>
public sealed class NoWalletOwnershipProof : IWalletOwnershipProof
{
    /// <inheritdoc/>
    public Task<bool> IsSignatureProvenAsync(Guid walletId, Guid ownerUserId, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>WTT's ownership seam over L4's persisted wallet-ownership challenge proof (<c>wallet_ownership_proof</c>).</summary>
public sealed class WalletOwnershipProofAdapter(IWalletOwnershipProofStore proofs) : IWalletOwnershipProof
{
    /// <inheritdoc/>
    public Task<bool> IsSignatureProvenAsync(Guid walletId, Guid ownerUserId, CancellationToken ct = default)
        => proofs.IsSignatureProvenAsync(walletId, ownerUserId, ct);
}
