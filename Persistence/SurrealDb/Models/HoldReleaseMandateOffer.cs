using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("hold_release_mandate_offer", Aggregate = "Immutable release mandate cost disclosure", Guardrail = "Server quoted original owner terms")]
[Slice("identity")]
public sealed class HoldReleaseMandateOffer : ISurrealRecord
{
    public string SchemaName => "hold_release_mandate_offer";
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string HoldRecordId { get; set; } = string.Empty;
    public string InputHash { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
