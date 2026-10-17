using AZOA.WebAPI.Interfaces.Wtt;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Fail-closed default until the wallet ownership challenge persists its proof flag: no External wallet is proven.</summary>
public sealed class NoWalletOwnershipProof : IWalletOwnershipProof
{
    /// <inheritdoc/>
    public Task<bool> IsSignatureProvenAsync(Guid walletId, Guid ownerUserId, CancellationToken ct = default) => Task.FromResult(false);
}
