using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable fungible creation admission and exact-wire recovery journal.</summary>
[SurrealTable("fungible_creation_operation", Aggregate = "Fungible creation", Guardrail = "G6 SCHEMAFULL")]
[Slice("wallet_nft")]
[Index("fungible_creation_actor", Fields = new[] { "actor_user_id", "created_at" })]
public partial class FungibleCreationOperation : ISurrealRecord
{
    public const string SchemaNameConst = "fungible_creation_operation";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string IntentJson { get; set; } = string.Empty;
    public string State { get; set; } = "Admitted";
    public long Version { get; set; }
    public int Generation { get; set; }
    public DateTimeOffset QuoteExpiresAt { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public string? OfferedQuoteJson { get; set; }
    public string? CurrentAuthorityId { get; set; }
    public string? CurrentAuthorityJson { get; set; }
    public string? PreparedJson { get; set; }
    public string HistoryJson { get; set; } = "[]";
    public string? ObservationJson { get; set; }
    public string? AssetId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
