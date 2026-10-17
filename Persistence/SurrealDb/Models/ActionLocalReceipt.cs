using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("action_local_receipt", Aggregate = "Atomic local action effects", Guardrail = "Immutable command receipt")]
[Slice("action")]
public sealed class ActionLocalReceipt : ISurrealRecord
{
    public string SchemaName => "action_local_receipt";
    [Id] public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string IntentHash { get; set; } = string.Empty;
    public string OutputJson { get; set; } = string.Empty;
    public bool IsError { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
