using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("consent_lifecycle_command", Aggregate = "Immutable consent command receipt")]
[Index("consent_command_grantor", Fields = new[] { "grantor_id", "created_at" })]
public sealed class ConsentLifecycleCommandReceipt : ISurrealRecord
{
    public string SchemaName => "consent_lifecycle_command";
    [Id] public string Id { get; set; } = string.Empty;
    public string GrantorId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string GrantId { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string AuthorityJson { get; set; } = string.Empty;
    public string ReceiptJson { get; set; } = string.Empty;
    public bool Withdrawn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
