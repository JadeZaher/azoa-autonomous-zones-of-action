using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("governance_proposal", Aggregate = "Immutable tenant election authority", Guardrail = "G6 SCHEMAFULL")]
[Slice("identity")]
public sealed class GovernanceProposal : ISurrealRecord
{
    public string SchemaName => "governance_proposal";
    [Id] public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string CreatorId { get; set; } = string.Empty;
    public string DefinitionHash { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = string.Empty;
    public string ElectorateJson { get; set; } = "[]";
    public string ElectorateHash { get; set; } = string.Empty;
    public int MembershipRuleVersion { get; set; } = 1;
    public string UniquenessScope { get; set; } = string.Empty;
    public string OperationKey { get; set; } = string.Empty;
    public DateTimeOffset OpensAt { get; set; }
    public DateTimeOffset ClosesAt { get; set; }
    public int MinimumVotes { get; set; }
    public long Version { get; set; }
    public string? FinalResultJson { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
