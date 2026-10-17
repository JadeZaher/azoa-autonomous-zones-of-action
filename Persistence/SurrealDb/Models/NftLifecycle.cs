using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Durable NFT intent and prepared transaction recovery journal.</summary>
[SurrealTable("nft_lifecycle", Aggregate = "NFT lifecycle", Guardrail = "G6 SCHEMAFULL")]
[Slice("wallet_nft")]
[Index("nft_lifecycle_actor_created", Fields = new[] { "actor_user_id", "created_at" })]
[Index("nft_lifecycle_recipient_created", Fields = new[] { "recipient_user_id", "created_at" })]
public partial class NftLifecycle : ISurrealRecord
{
    public const string SchemaNameConst = "nft_lifecycle";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string RecipientUserId { get; set; } = string.Empty;
    public string NftId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string ChainType { get; set; } = string.Empty;
    public string Network { get; set; } = string.Empty;
    public string Stage { get; set; } = "Create";
    public string State { get; set; } = "Preparing";
    public long Version { get; set; }
    public int PreparationGeneration { get; set; }
    public int ConfirmedStageCount { get; set; }
    public string? PreparedTransactionJson { get; set; }
    public string TransactionHistoryJson { get; set; } = "[]";
    public string StageConsentsJson { get; set; } = "[]";
    public string ReplacementCostConsentsJson { get; set; } = "[]";
    public string ReplacementCostRetirementsJson { get; set; } = "[]";
    public string ActorContinuationsJson { get; set; } = "[]";
    public string? AssetId { get; set; }
    public string? ProtocolIdentity { get; set; }
    public string? ContractAddress { get; set; }
    public string? TokenStandard { get; set; }
    public string? CreationTransactionId { get; set; }
    public string? FinalTransactionId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
