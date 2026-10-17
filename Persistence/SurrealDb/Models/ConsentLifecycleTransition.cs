using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("consent_lifecycle_transition", Aggregate = "Immutable consent transition evidence")]
[Index("consent_transition_grant", Fields = new[] { "grant_id" })]
public sealed class ConsentLifecycleTransition : ISurrealRecord
{
    public string SchemaName => "consent_lifecycle_transition";
    [Id] public string Id { get; set; } = string.Empty;
    public string GrantId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string GrantReceiptJson { get; set; } = string.Empty;
    public string AuthorityJson { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string BodyHash { get; set; } = string.Empty;
    public string? RegistrationId { get; set; }
    public long? DestinationRevision { get; set; }
    public string? DestinationUrl { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
