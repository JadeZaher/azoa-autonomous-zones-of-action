using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("hold_mandate_use_receipt", Aggregate = "Immutable release mandate stage authority", Guardrail = "Exact persisted authority and stage CAS")]
[Slice("identity")]
[Index("hold_mandate_use_operation", Fields = new[] { "operation_id", "command_version" }, Unique = true)]
public sealed class HoldMandateUseReceipt : ISurrealRecord
{
    public string SchemaName => "hold_mandate_use_receipt";
    [Id] public string Id { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public long CommandVersion { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string MandateId { get; set; } = string.Empty;
    public long MandateVersion { get; set; }
    public string TermsHash { get; set; } = string.Empty;
    public string AuthorityJson { get; set; } = string.Empty;
    public string? WorkflowSourceJson { get; set; }
    public string? ExchangeSourceJson { get; set; }
    public string? AuthorizationId { get; set; }
    public string? PreparedHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
