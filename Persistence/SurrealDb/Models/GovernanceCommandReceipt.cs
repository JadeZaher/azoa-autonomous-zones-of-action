using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("governance_command_receipt", Aggregate = "Atomic governance command receipt", Guardrail = "G6 SCHEMAFULL")]
[Slice("identity")]
public sealed class GovernanceCommandReceipt : ISurrealRecord
{
    public string SchemaName => "governance_command_receipt";
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string ProposalId { get; set; } = string.Empty;
    public string OutputJson { get; set; } = string.Empty;
    public bool IsError { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
