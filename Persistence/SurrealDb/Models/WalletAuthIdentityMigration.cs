using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Distributed readiness and lease for canonical wallet identity migration.</summary>
[SurrealTable("wallet_auth_identity_migration")]
[Slice("identity")]
public partial class WalletAuthIdentityMigration : ISurrealRecord
{
    public string SchemaName => "wallet_auth_identity_migration";
    [Id] public string Id { get; set; } = "v1";
    public string Status { get; set; } = "Pending";
    public string? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string DiagnosticsJson { get; set; } = "[]";
    public string AuditJson { get; set; } = "[]";
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
