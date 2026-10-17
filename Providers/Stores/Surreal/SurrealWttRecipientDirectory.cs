using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealWttRecipientDirectory(ISurrealExecutor executor) : IWttRecipientDirectory
{
    /// <inheritdoc/>
    /// <remarks>Participant-step routes are Algorand and Solana, whose canonical addresses compare exactly; see
    /// Services/Wormhole/AGENTS.md §Recipient rule.</remarks>
    public async Task<bool> IsAnotherUsersCustodialWalletAsync(Guid actor, string chainType, string address, CancellationToken ct = default)
    {
        if (actor == Guid.Empty || string.IsNullOrWhiteSpace(chainType) || string.IsNullOrWhiteSpace(address)) return false;
        var rows = await executor.QueryAsync<Wallet>(SurrealQuery<Wallet>.From()
            .Where(row => row.ChainType == chainType && row.Address == address && row.WalletType == Wallet.WalletTypeKind.Platform
                && row.EncryptedPrivateKey != null).Limit(16), ct);
        var self = actor.ToString("N");
        return rows.Any(row => SurrealRecordGuid.BareId(row.UserId) != self);
    }
}
