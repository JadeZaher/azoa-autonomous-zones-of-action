using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable operator request for one exact action recovery generation.</summary>
[SurrealTable("action_recovery_audit", Aggregate = "Action recovery", Guardrail = "G6 SCHEMAFULL")]
[Slice("action")]
[Index("action_recovery_audit_execution", Fields = new[] { "execution_id", "created_at" })]
public partial class ActionRecoveryAudit : ISurrealRecord
{
    public const string SchemaNameConst = "action_recovery_audit";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string OperatorId { get; set; } = string.Empty;
    public string ExecutionId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public long ExpectedVersion { get; set; }
    public string IntentHash { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Action { get; set; } = "RetryRecovery";
    public DateTimeOffset CreatedAt { get; set; }
}
