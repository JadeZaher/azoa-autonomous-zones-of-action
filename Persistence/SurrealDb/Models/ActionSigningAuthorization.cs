using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable participant authorization of one previously frozen workflow node.</summary>
[SurrealTable("action_signing_authorization", Aggregate = "Action signing authority", Guardrail = "G6 SCHEMAFULL")]
[Slice("action")]
[Index("action_signing_authorization_execution", Fields = new[] { "execution_id", "created_at" })]
public partial class ActionSigningAuthorization : ISurrealRecord
{
    public const string SchemaNameConst = "action_signing_authorization";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string ExecutionId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public long ExpectedVersion { get; set; }
    public string IntentHash { get; set; } = string.Empty;
    public string WitnessJson { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
