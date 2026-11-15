using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("fiat_payout_request_fence", Aggregate = "Cash-out admission or permanent void", Guardrail = "G6 SCHEMAFULL; same id as fiat_payout")]
[Slice("identity")]
public sealed partial class FiatPayoutRequestFence : ISurrealRecord
{
    public const string SchemaNameConst = "fiat_payout_request_fence";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("subject_user_id")] public string SubjectUserId { get; set; } = string.Empty;
    [JsonPropertyName("tenant_id")] public string? TenantId { get; set; }
    [JsonPropertyName("request_key")] public string RequestKey { get; set; } = string.Empty;
    [JsonPropertyName("environment")] public string Environment { get; set; } = string.Empty;
    [JsonPropertyName("chain_network")] public string ChainNetwork { get; set; } = string.Empty;
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
}
