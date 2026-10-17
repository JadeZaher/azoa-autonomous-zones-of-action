using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Helpers;

/// <summary>Matches the complete observed wallet binding and account authority, including absent values.</summary>
public static class WalletUserAuthenticationCas
{
    public static TypedUpdateOnlyBuilder<User> Match(User user)
    {
        var update = SurrealWriter.UpdateOnly<User>(user.Id);
        update = user.AuthWalletAddress is null ? update.Where(row => row.AuthWalletAddress == null) : update.Where(row => row.AuthWalletAddress == user.AuthWalletAddress);
        update = user.AuthWalletChainType is null ? update.Where(row => row.AuthWalletChainType == null) : update.Where(row => row.AuthWalletChainType == user.AuthWalletChainType);
        update = user.OwnerTenantId is null ? update.Where(row => row.OwnerTenantId == null) : update.Where(row => row.OwnerTenantId == user.OwnerTenantId);
        update = user.AuthNotBefore is null ? update.Where(row => row.AuthNotBefore == null) : update.Where(row => row.AuthNotBefore == user.AuthNotBefore);
        return update;
    }
}
