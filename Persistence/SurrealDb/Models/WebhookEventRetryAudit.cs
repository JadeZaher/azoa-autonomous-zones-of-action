using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("webhook_event_retry_audit", Aggregate = "Webhook event retry", Guardrail = "Immutable authorized exact-command receipt")]
[Slice("bridge")]
[Index("webhook_event_retry_tenant", Fields = new[] { "tenant_id", "created_at" })]
public sealed class WebhookEventRetryAudit : ISurrealRecord
{
    public string SchemaName => "webhook_event_retry_audit";
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string AuthorityJson { get; set; } = string.Empty;
    public string ReceiptJson { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
