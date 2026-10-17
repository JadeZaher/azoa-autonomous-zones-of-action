using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Server-issued native-operation resource quote bound to exact authority and intent.</summary>
[SurrealTable("native_quote", Aggregate = "Deployment", Guardrail = "G6 SCHEMAFULL")]
[Slice("deployment")]
public partial class NativeQuote : ISurrealRecord
{
    public const string SchemaNameConst = "native_quote";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string QuoteJson { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}
