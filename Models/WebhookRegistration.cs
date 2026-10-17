using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Models;

/// <summary>Tenant webhook destination and protected signing material; see Services/Webhooks/AGENTS.md.</summary>
public sealed class WebhookRegistration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Url { get; set; } = string.Empty;
    [JsonIgnore] public string Secret { get; set; } = string.Empty;
    [JsonIgnore] public string EncryptedSecret { get; set; } = string.Empty;
    public string? LegacyFingerprint { get; set; }
    public long Version { get; set; }
    public long DestinationRevision { get; set; }
    public long SecretRevision { get; set; }
    public DateTime? SecretRotatedAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
