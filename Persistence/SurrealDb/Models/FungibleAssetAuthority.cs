using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Original confirmed fungible issuance provenance, independent of mutable asset roles.</summary>
[SurrealTable("fungible_asset_authority", Aggregate = "Fungible creation", Guardrail = "G6 SCHEMAFULL")]
[Slice("wallet_nft")]
[Index("fungible_asset_identity", Fields = new[] { "chain_type", "network", "protocol_identity", "asset_id" }, Unique = true)]
[Index("fungible_asset_operation", Fields = new[] { "operation_id" }, Unique = true)]
public partial class FungibleAssetAuthority : ISurrealRecord
{
    public const string SchemaNameConst = "fungible_asset_authority";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string IssuerUserId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string ChainType { get; set; } = string.Empty;
    public string Network { get; set; } = string.Empty;
    public string ProtocolIdentity { get; set; } = string.Empty;
    public string AssetId { get; set; } = string.Empty;
    public string CreatorAddress { get; set; } = string.Empty;
    public string CreationTransactionId { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string IntentJson { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
