using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>Checks the shared monotonic readiness gate before wallet identity reads or writes.</summary>
public static class WalletAuthIdentityGate
{
    public static async Task<bool> IsReadyAsync(ISurrealExecutor executor, CancellationToken ct)
        => (await executor.QuerySingleAsync<WalletAuthIdentityMigration>(SurrealQuery<WalletAuthIdentityMigration>.Key("v1"), ct))?.Status == "Ready";
}
