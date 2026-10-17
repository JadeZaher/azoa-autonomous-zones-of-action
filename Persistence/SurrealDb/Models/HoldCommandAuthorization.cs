using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("hold_command_authorization", Aggregate = "Exact hold continuation consent", Guardrail = "Immutable original operation, version, quote and authority receipt")]
[Slice("identity")]
[Index("hold_command_authorization_operation", Fields = new[] { "operation_id", "created_at" })]
public sealed class HoldCommandAuthorization : ISurrealRecord
{
    public string SchemaName => "hold_command_authorization";
    [Id] public string Id { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Kind { get; set; } = "Transfer";
    public string ActorUserId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public long ExpectedVersion { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string QuoteId { get; set; } = string.Empty;
    public string MaximumNetworkFeeBaseUnits { get; set; } = string.Empty;
    public string AuthorityJson { get; set; } = string.Empty;
    public string? PreparedHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
