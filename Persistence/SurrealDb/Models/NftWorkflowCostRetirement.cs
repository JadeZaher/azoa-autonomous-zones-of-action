using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable retirement of an uncommitted workflow cost acceptance.</summary>
[SurrealTable("nft_workflow_cost_retirement", Aggregate = "NFT lifecycle", Guardrail = "G6 SCHEMAFULL")]
[Slice("wallet_nft")]
public partial class NftWorkflowCostRetirement : ISurrealRecord
{
    public const string SchemaNameConst = "nft_workflow_cost_retirement";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string? ActingTenantId { get; set; }
    public string ExecutionId { get; set; } = string.Empty;
    public long ExecutionVersion { get; set; }
    public string RequestJson { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string ReferenceJson { get; set; } = string.Empty;
    public string ConsentJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
