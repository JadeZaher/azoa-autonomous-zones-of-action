using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("consent_participation_approval", Aggregate = "Tenant-approved subject enrollment evidence")]
public sealed class ConsentParticipationApproval : ISurrealRecord
{
    public string SchemaName => "consent_participation_approval";
    [Id] public string Id { get; set; } = string.Empty;
    public string MembershipId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string GrantorId { get; set; } = string.Empty;
    public string SourceCommandId { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
