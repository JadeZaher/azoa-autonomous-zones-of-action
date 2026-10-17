using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Scoped immutable WTT intent, its admitted quote, recorded proofs and settlement projection.</summary>
[SurrealTable("wtt_operation", Aggregate = "Wtt", Guardrail = "G6 SCHEMAFULL")]
[Index("wtt_operation_owner", Fields = new[] { "actor_user_id", "tenant_id", "created_at" })]
[Index("wtt_operation_due", Fields = new[] { "state", "next_attempt_at" })]
[Slice("wtt")]
public partial class WttOperation : ISurrealRecord
{
    public const string SchemaNameConst = "wtt_operation";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string RequestKeyHash { get; set; } = string.Empty;
    public string OfferId { get; set; } = string.Empty;
    public string OfferHash { get; set; } = string.Empty;
    public string IntentJson { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string SourceQuoteJson { get; set; } = string.Empty;
    public string? TargetQuoteJson { get; set; }
    [Inside("Admitted", "Active", "Ready", "AwaitingProof", "Settled", "Cancelled", "NeedsOperator")]
    public string State { get; set; } = "Admitted";
    [Inside("Source", "Target", "NativeOutput", "Complete")]
    public string Phase { get; set; } = "Source";
    public string? CurrentStageId { get; set; }
    public string? ResumeState { get; set; }
    public string? TransferPublicationJson { get; set; }
    public string? AttestationPublicationJson { get; set; }
    public string? TransferEnvelopeBase64 { get; set; }
    public string? AttestationEnvelopeBase64 { get; set; }
    public string? ProofsJson { get; set; }
    public string? BodyClaimId { get; set; }
    public string? SettlementJson { get; set; }
    public string? Detail { get; set; }
    public string? ReturnOfOperationId { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
}
