using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable server-issued WTT offer; admission consumes it once before its provider quote expires.</summary>
[SurrealTable("wtt_offer", Aggregate = "Wtt", Guardrail = "G6 SCHEMAFULL")]
[Index("wtt_offer_owner", Fields = new[] { "actor_user_id", "tenant_id", "created_at" })]
[Slice("wtt")]
public partial class WttOffer : ISurrealRecord
{
    public const string SchemaNameConst = "wtt_offer";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string RequestKeyHash { get; set; } = string.Empty;
    public string IntentJson { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string SourceQuoteJson { get; set; } = string.Empty;
    public string TargetInspectionJson { get; set; } = string.Empty;
    public string OfferHash { get; set; } = string.Empty;
    public string? ReturnOfOperationId { get; set; }
    public string? ConsumedByOperationId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
