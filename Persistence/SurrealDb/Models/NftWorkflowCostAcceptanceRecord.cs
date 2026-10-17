using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable workflow cost acceptance created atomically with its NFT lifecycle.</summary>
[SurrealTable("nft_workflow_cost_acceptance", Aggregate = "NFT lifecycle", Guardrail = "G6 SCHEMAFULL")]
[Slice("wallet_nft")]
public partial class NftWorkflowCostAcceptanceRecord : ISurrealRecord
{
    public const string SchemaNameConst = "nft_workflow_cost_acceptance";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string? ActingTenantId { get; set; }
    public string ExecutionId { get; set; } = string.Empty;
    public long ExecutionVersion { get; set; }
    public string LifecycleId { get; set; } = string.Empty;
    public string FrozenInputHash { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string ReceiptHash { get; set; } = string.Empty;
    public string ReceiptJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
