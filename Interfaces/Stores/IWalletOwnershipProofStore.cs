using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Signature-verified wallet control proofs, one current prover per canonical (chain, address).</summary>
public interface IWalletOwnershipProofStore
{
    /// <summary>The current proof for the address, or null; the address is canonicalized first and an invalid one yields null.</summary>
    Task<WalletOwnershipProof?> GetAsync(string chainType, string address, CancellationToken ct = default);

    /// <summary>True only when <paramref name="walletId"/> is an External wallet of <paramref name="ownerUserId"/>
    /// whose canonical address that same user currently holds a signature proof for (the WTT recipient seam).</summary>
    Task<bool> IsSignatureProvenAsync(Guid walletId, Guid ownerUserId, CancellationToken ct = default);

    /// <summary>Records (or supersedes) the proof; callers must only pass an address whose challenge signature just verified.</summary>
    Task<WalletOwnershipProof> RecordAsync(WalletOwnershipProof proof, CancellationToken ct = default);
}
