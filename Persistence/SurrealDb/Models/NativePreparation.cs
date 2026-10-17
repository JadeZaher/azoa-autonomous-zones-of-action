using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable provider plan admitted before custody and its single canonical signed candidate.</summary>
[SurrealTable("native_preparation", Aggregate = "Deployment", Guardrail = "G6 SCHEMAFULL")]
[Slice("deployment")]
public partial class NativePreparation : ISurrealRecord
{
    public const string SchemaNameConst = "native_preparation";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public int Generation { get; set; }
    public string PlanJson { get; set; } = string.Empty;
    public string? PreparedJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
