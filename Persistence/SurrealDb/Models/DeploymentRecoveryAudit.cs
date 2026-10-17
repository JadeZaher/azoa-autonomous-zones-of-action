using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Durable bounded-page inspection cursor for one immutable recovery ancestry.</summary>
[SurrealTable("deployment_recovery_audit", Aggregate = "Deployment", Guardrail = "G6 SCHEMAFULL")]
[Slice("deployment")]
public partial class DeploymentRecoveryAudit : ISurrealRecord
{
    public string SchemaName => "deployment_recovery_audit";
    [Id] public string Id { get; set; } = string.Empty;
    public string ParentOperationId { get; set; } = string.Empty;
    public string HeadOperationId { get; set; } = string.Empty;
    public string HeadIntentHash { get; set; } = string.Empty;
    public string? HeadPredecessorId { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string? NextOperationId { get; set; }
    public string ExpectedSuccessorId { get; set; } = string.Empty;
    public string Digest { get; set; } = string.Empty;
    public long InspectedCount { get; set; }
    public string State { get; set; } = "Running";
    public string? Detail { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Immutable visited-node evidence that detects ancestry cycles across pages.</summary>
[SurrealTable("deployment_recovery_audit_entry", Aggregate = "Deployment", Guardrail = "G6 SCHEMAFULL")]
[Slice("deployment")]
[Index("deployment_recovery_audit_visited", Fields = new[] { "audit_id", "operation_id" }, Unique = true)]
public partial class DeploymentRecoveryAuditEntry : ISurrealRecord
{
    public string SchemaName => "deployment_recovery_audit_entry";
    [Id] public string Id { get; set; } = string.Empty;
    public string AuditId { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string SuccessorId { get; set; } = string.Empty;
    public string? PredecessorId { get; set; }
    public string IntentHash { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string HistoryHash { get; set; } = string.Empty;
    public long OperationVersion { get; set; }
    public long Position { get; set; }
    public string Digest { get; set; } = string.Empty;
}
