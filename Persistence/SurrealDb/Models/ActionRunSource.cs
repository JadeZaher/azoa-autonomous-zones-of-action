using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("action_run_source", Aggregate = "Frozen workflow source admission")]
[Index("action_run_source_identity", Fields = new[] { "run_id", "action_id" }, Unique = true)]
public sealed class ActionRunSource : ISurrealRecord
{
    public const string SchemaNameConst = "action_run_source";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    [References(typeof(ActionRun))] public string RunId { get; set; } = string.Empty;
    [References(typeof(ActionDefinition))] public string ActionId { get; set; } = string.Empty;
    public long SourceVersion { get; set; }
}
