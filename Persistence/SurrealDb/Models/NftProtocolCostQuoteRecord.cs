using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable server cost offer; lifecycle admission separately fences authority and database expiry.</summary>
[SurrealTable("nft_protocol_cost_quote", Aggregate = "NFT lifecycle", Guardrail = "G6 SCHEMAFULL")]
[Slice("wallet_nft")]
public partial class NftProtocolCostQuoteRecord : ISurrealRecord
{
    public const string SchemaNameConst = "nft_protocol_cost_quote";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string ActingTenantId { get; set; } = string.Empty;
    public string LifecycleId { get; set; } = string.Empty;
    public string QuoteHash { get; set; } = string.Empty;
    public string QuoteJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
