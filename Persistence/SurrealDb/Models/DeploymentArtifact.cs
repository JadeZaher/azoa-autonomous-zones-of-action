using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable generated executable artifact and its noncircular content identity.</summary>
[SurrealTable("deployment_artifact", Aggregate = "Deployment", Guardrail = "G6 SCHEMAFULL")]
[Slice("deployment")]
[Index("deployment_artifact_owner", Fields = new[] { "owner_user_id", "created_at" })]
public partial class DeploymentArtifact : ISurrealRecord
{
    public const string SchemaNameConst = "deployment_artifact";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string BundleId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public string ArtifactHash { get; set; } = string.Empty;
    public string? Loader { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
