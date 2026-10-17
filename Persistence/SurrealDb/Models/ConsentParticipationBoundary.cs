using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("consent_participation_boundary", Aggregate = "Consent membership admission and offboarding boundary")]
public sealed class ConsentParticipationBoundary : ISurrealRecord
{
    public string SchemaName => "consent_participation_boundary";
    [Id] public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ParticipationRef { get; set; } = string.Empty;
    [Default("0")] public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosureReceiptJson { get; set; }
    public string? ClosureAuthorityJson { get; set; }
}
