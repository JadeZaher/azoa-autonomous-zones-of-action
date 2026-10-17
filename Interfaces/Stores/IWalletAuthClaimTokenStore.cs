using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Creates and resolves invites; IUserStore consumes them atomically with ownership changes.</summary>
public interface IWalletAuthClaimTokenStore
{
    /// <summary>Creates a unique claim invitation.</summary>
    Task<AZOAResult<WalletAuthClaimToken>> CreateAsync(WalletAuthClaimToken token, CancellationToken ct = default);

    /// <summary>Resolves an invitation without consuming it.</summary>
    Task<AZOAResult<WalletAuthClaimToken>> GetByTokenAsync(string token, CancellationToken ct = default);
}
