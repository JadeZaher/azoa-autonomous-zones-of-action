using System.Security.Cryptography;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.IntegrationTests.Deployments;

internal sealed class LocalNativeCustody(SigningContext owner, Func<byte[]> exportKey) : IKeyCustodyService
{
    internal int Signatures { get; private set; }
    public Task<AZOAResult<T>> WithSigningKeyAsync<T>(Guid walletId, Guid userId, Func<byte[], Task<T>> sign)
        => WithSigningKeyAsync(SigningContext.ForUser(userId, walletId).ForChain(owner.ChainType!), sign);
    public async Task<AZOAResult<T>> WithSigningKeyAsync<T>(SigningContext context, Func<byte[], Task<T>> sign)
    {
        if (context.IsPlatform || context.IsTenantDriven || context.UserId != owner.UserId || context.WalletId != owner.WalletId
            || context.ChainType != owner.ChainType || context.Scope is not (AzoaScopes.ContractDeploySign or AzoaScopes.ContractCallSign or AzoaScopes.SolanaAccountSign))
            return AZOAResult<T>.Failure("Only the local fixture actor and explicit native scope may sign.");
        var key = exportKey();
        try { Signatures++; return AZOAResult<T>.Success(await sign(key)); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(bool permitted, Func<byte[], Task<T>> sign)
        => Task.FromResult(AZOAResult<T>.Failure("Local native fixtures never use platform authority."));
    public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(bool permitted, SigningContext context, Func<byte[], Task<T>> sign)
        => WithPlatformSigningKeyAsync(permitted, sign);
    public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(string chain, bool permitted, SigningContext context, Func<byte[], Task<T>> sign)
        => WithPlatformSigningKeyAsync(permitted, sign);
    public Task<AZOAResult<bool>> CanSignAsync(Guid walletId, Guid userId)
        => Task.FromResult(AZOAResult<bool>.Success(walletId == owner.WalletId && userId == owner.UserId));
    public AZOAResult<IWallet> RewrapAsync(IWallet wallet, WalletKeyService oldKeyService, WalletKeyService newKeyService)
        => AZOAResult<IWallet>.Failure("Fixture keys are ephemeral.");
}
