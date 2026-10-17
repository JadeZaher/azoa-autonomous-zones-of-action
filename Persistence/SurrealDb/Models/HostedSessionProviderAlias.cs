using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("hosted_session_provider_alias", Aggregate = "HostedSession", Guardrail = "G6 SCHEMAFULL")]
[Slice("identity")]
public sealed partial class HostedSessionProviderAlias : ISurrealRecord
{
    public const string SchemaNameConst = "hosted_session_provider_alias";
    public string SchemaName => SchemaNameConst;
    [Id]
    public string Id { get; set; } = string.Empty;
    public string HostedSessionId { get; set; } = string.Empty;
    public string FlowKind { get; set; } = string.Empty;
    public string ProviderKey { get; set; } = string.Empty;
    public string ProviderSessionId { get; set; } = string.Empty;
}
