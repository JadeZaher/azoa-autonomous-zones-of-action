using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Five durable challenge slots per canonical wallet identity.</summary>
[SurrealTable("wallet_auth_challenge_budget")]
[Slice("identity")]
public partial class WalletAuthChallengeBudget : ISurrealRecord
{
    public string SchemaName => "wallet_auth_challenge_budget";
    [Id] public string Id { get; set; } = string.Empty;
    public string SlotsJson { get; set; } = "[]";
    public long Version { get; set; }
}
