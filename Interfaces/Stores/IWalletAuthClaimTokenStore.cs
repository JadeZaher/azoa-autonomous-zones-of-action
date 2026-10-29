using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Creates and resolves invites; IUserStore consumes them atomically with ownership changes.</summary>
public interface IWalletAuthClaimTokenStore
{
    /// <summary>Creates a unique claim invitation; refuses any <see cref="WalletAuthClaimToken.TokenHash"/> that is not a v1 digest.</summary>
    Task<AZOAResult<WalletAuthClaimToken>> CreateAsync(WalletAuthClaimToken token, CancellationToken ct = default);

    /// <summary>Resolves an invitation by its stored v1 digest without consuming it; never matches a non-digest value.</summary>
    Task<AZOAResult<WalletAuthClaimToken>> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default);
}
