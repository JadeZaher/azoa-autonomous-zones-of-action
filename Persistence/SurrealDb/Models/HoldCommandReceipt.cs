using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("hold_command_receipt", Aggregate = "Hold terminal command evidence", Guardrail = "Immutable command outcome committed with custody accounting")]
[Slice("identity")]
[Index("hold_command_receipt_hold", Fields = new[] { "hold_record_id", "created_at" })]
public sealed class HoldCommandReceipt : ISurrealRecord
{
    public string SchemaName => "hold_command_receipt";
    [Id] public string Id { get; set; } = string.Empty;
    public string HoldRecordId { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string OutcomeJson { get; set; } = string.Empty;
    public string? EvidenceJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
