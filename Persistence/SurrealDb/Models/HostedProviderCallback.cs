using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Authenticated callback identity without provider payload or identity attributes.</summary>
[SurrealTable("hosted_provider_callback")]
[Slice("identity")]
public partial class HostedProviderCallback : ISurrealRecord
{
    public const string SchemaNameConst = "hosted_provider_callback";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string AttemptId { get; set; } = string.Empty;
    public string BodyDigest { get; set; } = string.Empty;
    public bool Processed { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
