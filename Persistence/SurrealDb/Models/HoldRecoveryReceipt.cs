using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("hold_recovery_receipt", Aggregate = "Hold recovery audit", Guardrail = "Immutable original command, expected version and recovery result")]
[Slice("identity")]
[Index("hold_recovery_operation", Fields = new[] { "operation_id", "created_at" })]
public sealed class HoldRecoveryReceipt : ISurrealRecord
{
    public string SchemaName => "hold_recovery_receipt";
    [Id] public string Id { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string AuthorityJson { get; set; } = string.Empty;
    public string OriginalState { get; set; } = string.Empty;
    public long OriginalVersion { get; set; }
    public string OutcomeJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
