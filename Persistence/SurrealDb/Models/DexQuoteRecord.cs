using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable authenticated DEX quote; consumption is fenced by the swap admission transaction.</summary>
[SurrealTable("dex_quote", Aggregate = "Swap", Guardrail = "G6 SCHEMAFULL")]
[Index("dex_quote_actor", Fields = new[] { "actor_user_id", "tenant_id", "created_at" })]
[Slice("swap")]
public partial class DexQuoteRecord : ISurrealRecord
{
    public const string SchemaNameConst = "dex_quote";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string QuoteHash { get; set; } = string.Empty;
    public string QuoteJson { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ConsumedByOperationId { get; set; }
}
