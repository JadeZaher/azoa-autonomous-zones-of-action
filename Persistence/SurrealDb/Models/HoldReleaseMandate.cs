using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("hold_release_mandate", Aggregate = "Exact automatic custody release consent", Guardrail = "Immutable terms and first-egress authority")]
[Slice("identity")]
[Index("hold_release_mandate_hold", Fields = new[] { "hold_record_id", "created_at" })]
[Index("hold_release_mandate_due", Fields = new[] { "status", "next_attempt_at" })]
public sealed class HoldReleaseMandate : ISurrealRecord
{
    public string SchemaName => "hold_release_mandate";
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string HoldRecordId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string TermsJson { get; set; } = string.Empty;
    public string TermsHash { get; set; } = string.Empty;
    public string OriginalAuthorityJson { get; set; } = string.Empty;
    public string CurrentAuthorityJson { get; set; } = string.Empty;
    public string? CurrentAuthorizationId { get; set; }
    public string OriginalQuoteJson { get; set; } = string.Empty;
    public string? WorkflowSourceJson { get; set; }
    public string? ExchangeSourceJson { get; set; }
    public string Status { get; set; } = "Active";
    public string? OperationId { get; set; }
    public string? Detail { get; set; }
    public long Version { get; set; }
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
