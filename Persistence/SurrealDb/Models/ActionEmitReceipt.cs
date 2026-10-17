using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("action_emit_receipt", Aggregate = "Durable Emit acceptance", Guardrail = "Immutable receipt and outbox commit together")]
[Slice("action")]
public sealed class ActionEmitReceipt : ISurrealRecord
{
    public string SchemaName => "action_emit_receipt";
    [Id] public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string ActionId { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string OutputJson { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? EventId { get; set; }
    public string? EventBodyHash { get; set; }
    public string? RegistrationId { get; set; }
    public long? DestinationRevision { get; set; }
    public string? DestinationUrl { get; set; }
    public bool IsError { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
