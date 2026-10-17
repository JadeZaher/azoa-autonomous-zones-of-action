using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable exact provider request and bounded recovery ownership for one hosted attempt.</summary>
[SurrealTable("hosted_provider_attempt")]
[Slice("identity")]
[Index("hosted_provider_remote", Fields = new[] { "adapter_key", "origin", "remote_session_id" }, Unique = true)]
public partial class HostedProviderAttempt : ISurrealRecord
{
    public const string SchemaNameConst = "hosted_provider_attempt";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string AdapterKey { get; set; } = string.Empty;
    public string ProviderKey { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string PolicyRevision { get; set; } = string.Empty;
    public string ProtocolVersion { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string RequestDigest { get; set; } = string.Empty;
    public DateTimeOffset? SendStartedAt { get; set; }
    public string? RemoteSessionId { get; set; }
    public string? RedirectUrl { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public bool Quarantined { get; set; }
    public bool RecoveryCompleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; }
}
