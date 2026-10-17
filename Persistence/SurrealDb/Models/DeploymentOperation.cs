using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable deployment admission and exact native-stage recovery journal.</summary>
[SurrealTable("deployment_operation", Aggregate = "Deployment", Guardrail = "G6 SCHEMAFULL")]
[Slice("deployment")]
[Index("deployment_operation_owner", Fields = new[] { "actor_user_id", "created_at" })]
[Index("deployment_operation_state", Fields = new[] { "state", "updated_at" })]
public partial class DeploymentOperation : ISurrealRecord
{
    public const string SchemaNameConst = "deployment_operation";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ArtifactId { get; set; } = string.Empty;
    public string ArtifactHash { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string? ActionRunId { get; set; }
    public string? ActionNodeId { get; set; }
    public string? ActionIntentHash { get; set; }
    public string State { get; set; } = "Admitted";
    public string Stage { get; set; } = string.Empty;
    public string? PreparedJson { get; set; }
    public bool SubmissionAuthorized { get; set; }
    public string HistoryJson { get; set; } = "[]";
    public string? ResourceId { get; set; }
    public string? EvidenceJson { get; set; }
    public string? Detail { get; set; }
    public string RecoveryMode { get; set; } = "None";
    public string? RecoveryChildId { get; set; }
    public string? ParentOperationId { get; set; }
    public string? RecoveryBindingHash { get; set; }
    public string? RecoveryEvidenceJson { get; set; }
    public bool RecoveryProjected { get; set; }
    public string? ReplacesOperationId { get; set; }
    public string? ReplacedByOperationId { get; set; }
    public long Version { get; set; }
    public int Generation { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
