using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("hold_command", Aggregate = "Durable hold command", Guardrail = "Immutable intent and unique custody command")]
[Slice("identity")]
[Index("hold_command_actor", Fields = new[] { "actor_user_id", "tenant_id", "created_at" })]
[Index("hold_command_hold", Fields = new[] { "hold_record_id", "created_at" })]
[Index("hold_command_recipient", Fields = new[] { "recipient_user_id", "created_at" })]
[Index("hold_command_recovery", Fields = new[] { "state", "updated_at" })]
[Index("hold_command_due", Fields = new[] { "next_reconcile_at", "id" })]
public sealed class HoldCommand : ISurrealRecord
{
    public string SchemaName => "hold_command";
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string? RecipientUserId { get; set; }
    public string HoldRecordId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string IntentJson { get; set; } = string.Empty;
    public string AuthorityJson { get; set; } = string.Empty;
    public string State { get; set; } = "Admitted";
    public string? Stage { get; set; }
    public long Generation { get; set; }
    public string? StageIntentJson { get; set; }
    public string? RailPlanJson { get; set; }
    public string? WorkflowMandateDraftJson { get; set; }
    public string? AcceptedQuoteId { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public string? CurrentAuthorityJson { get; set; }
    public string? CurrentAuthorizationId { get; set; }
    public string TransactionHistoryJson { get; set; } = "[]";
    public string? PreparedJson { get; set; }
    public string? AcceptanceIntentJson { get; set; }
    public string? AcceptanceRequestId { get; set; }
    public string? AcceptanceRequestHash { get; set; }
    public string? AcceptanceAuthorityJson { get; set; }
    public string? CurrentAcceptanceAuthorityJson { get; set; }
    public string? CurrentAcceptanceAuthorizationId { get; set; }
    public string? AcceptanceState { get; set; }
    public string? AcceptancePreparedJson { get; set; }
    public string? AcceptanceEvidenceJson { get; set; }
    public string? ObservationJson { get; set; }
    public string? Detail { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? NextReconcileAt { get; set; }
}
