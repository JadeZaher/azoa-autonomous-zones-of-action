using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("webhook_registration", Aggregate = "WebhookRegistration", Guardrail = "Tenant unique, encrypted versioned secret")]
[Slice("bridge")]
[Index("webhook_registration_tenant_unique", Fields = new[] { "tenant_id" }, Unique = true)]
public partial class WebhookRegistration : ISurrealRecord
{
    public const string SchemaNameConst = "webhook_registration";
    public string SchemaName => SchemaNameConst;
    [Id] [Required(NotEmpty = true)] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("tenant_id")] [References(typeof(User))] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("url")] [Required(NotEmpty = true)] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("secret")] public string? LegacySecret { get; set; }
    [JsonPropertyName("encrypted_secret")] public string EncryptedSecret { get; set; } = string.Empty;
    [JsonPropertyName("version")] public long Version { get; set; }
    [JsonPropertyName("destination_revision")] public long DestinationRevision { get; set; }
    [JsonPropertyName("secret_revision")] public long SecretRevision { get; set; }
    [JsonPropertyName("secret_rotated_at")] public DateTimeOffset? SecretRotatedAt { get; set; }
    [JsonPropertyName("is_active")] [Default("true")] public bool IsActive { get; set; }
    [JsonPropertyName("created_at")] [ReadOnly] public DateTimeOffset CreatedAt { get; set; }
}
