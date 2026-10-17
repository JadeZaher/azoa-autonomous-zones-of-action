using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Scoped immutable swap request and durable admission/cancellation authority.</summary>
[SurrealTable("swap_operation", Aggregate = "Swap", Guardrail = "G6 SCHEMAFULL")]
[Index("swap_operation_owner", Fields = new[] { "actor_user_id", "tenant_id", "created_at" })]
[Index("swap_operation_due", Fields = new[] { "state", "next_attempt_at" })]
[Slice("swap")]
public partial class SwapOperationRecord : ISurrealRecord
{
    public const string SchemaNameConst = "swap_operation";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string RequestKeyHash { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string QuoteId { get; set; } = string.Empty;
    public string QuoteHash { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string IntentJson { get; set; } = string.Empty;
    public string State { get; set; } = "Admitted";
    public string? CurrentStageId { get; set; }
    public int CostAccountingVersion { get; set; }
    public string ConfirmedEffectNetworkFee { get; set; } = "0";
    public string ConfirmedCleanupNetworkFee { get; set; } = "0";
    public string ConfirmedRetainedRent { get; set; } = "0";
    public string ConfirmedReleasedRent { get; set; } = "0";
    public string UnresolvedRentReservation { get; set; } = "0";
    public long UnresolvedRentStageCount { get; set; }
    public bool CleanupStarted { get; set; }
    public string? SettlementReceiptJson { get; set; }
    public string? CleanupConsentJson { get; set; }
    public string? CleanupReceiptJson { get; set; }
    public string? RecoveryHistoryCursor { get; set; }
    public string? ActionRunId { get; set; }
    public string? ActionNodeId { get; set; }
    public string? ActionIntentHash { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
}
