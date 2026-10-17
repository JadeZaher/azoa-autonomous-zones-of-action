using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("exchange_hold_source", Aggregate = "Exchange custody source", Guardrail = "Original purchase authority and exact hold commitment")]
[Slice("identity")]
[Index("exchange_hold_source_request", Fields = new[] { "tenant_user_id", "api_key_id", "request_key" }, Unique = true)]
[Index("exchange_hold_source_pending", Fields = new[] { "state", "updated_at" })]
public sealed class ExchangeHoldSource : ISurrealRecord
{
    public string SchemaName => "exchange_hold_source";
    [Id] public string Id { get; set; } = string.Empty;
    public string TenantUserId { get; set; } = string.Empty;
    public string ApiKeyId { get; set; } = string.Empty;
    public string ParticipantUserId { get; set; } = string.Empty;
    public string RequestKey { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string IntentJson { get; set; } = string.Empty;
    public string AuthorityJson { get; set; } = string.Empty;
    public string State { get; set; } = "Admitted";
    public string? HoldCommandId { get; set; }
    public string? HoldRecordId { get; set; }
    public string? WireIntentJson { get; set; }
    public string? PreparedWireJson { get; set; }
    public string? PreparedHash { get; set; }
    public string? SettlementEvidenceJson { get; set; }
    public long Generation { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
