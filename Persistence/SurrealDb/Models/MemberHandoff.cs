using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("member_handoff", Aggregate = "MemberHandoff", Guardrail = "G6 SCHEMAFULL")]
[Slice("identity")]
[Index("member_handoff_target", Fields = new[] { "tenant_id", "target_user_id" })]
public sealed partial class MemberHandoff : ISurrealRecord
{
    public const string SchemaNameConst = "member_handoff";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("tenant_id")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("target_user_id")] public string TargetUserId { get; set; } = string.Empty;
    [JsonPropertyName("external_subject")] public string ExternalSubject { get; set; } = string.Empty;
    [JsonPropertyName("descriptor")] public string Descriptor { get; set; } = string.Empty;
    [JsonPropertyName("delivery_email_digest")] public string DeliveryEmailDigest { get; set; } = string.Empty;
    [JsonPropertyName("protected_delivery")] public string ProtectedDelivery { get; set; } = string.Empty;
    [JsonPropertyName("token_digest")] public string? TokenDigest { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; set; }
    [JsonPropertyName("version")] public long Version { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
}
