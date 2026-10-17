using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("webhook_registration_audit", Aggregate = "Webhook registration", Guardrail = "Immutable exact-command receipt")]
[Slice("bridge")]
[Index("webhook_registration_audit_tenant", Fields = new[] { "tenant_id", "created_at" })]
public partial class WebhookRegistrationAudit : ISurrealRecord
{
    public const string SchemaNameConst = "webhook_registration_audit";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string RegistrationId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string AuthorityJson { get; set; } = string.Empty;
    [Default("'Applied'")] public string Disposition { get; set; } = "Applied";
    public string ReceiptJson { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
