using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Exclusive durable ownership of one physical-network ERC20 allowance.</summary>
[SurrealTable("ethereum_dex_allowance", Aggregate = "Swap", Guardrail = "G6 SCHEMAFULL")]
[Index("ethereum_dex_allowance_resource", Fields = new[] { "protocol_identity", "owner_address", "token_address", "executor_address" }, Unique = true)]
[Slice("swap")]
public partial class EthereumDexAllowance : ISurrealRecord
{
    public const string SchemaNameConst = "ethereum_dex_allowance";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ProtocolIdentity { get; set; } = string.Empty;
    public string OwnerAddress { get; set; } = string.Empty;
    public string TokenAddress { get; set; } = string.Empty;
    public string ExecutorAddress { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string State { get; set; } = "Held";
    public string? ReleaseStageId { get; set; }
    public string? ReleaseEvidenceJson { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
