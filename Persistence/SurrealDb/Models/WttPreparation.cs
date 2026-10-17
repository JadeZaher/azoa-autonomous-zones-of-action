using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>One winning unsigned protocol plan and its fill-once signed candidate.</summary>
[SurrealTable("wtt_preparation", Aggregate = "Wtt", Guardrail = "G6 SCHEMAFULL")]
[Slice("wtt")]
public partial class WttPreparation : ISurrealRecord
{
    public const string SchemaNameConst = "wtt_preparation";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string BindingJson { get; set; } = string.Empty;
    public string PlanJson { get; set; } = string.Empty;
    public string? SignedPreparationJson { get; set; }
    public bool Sealed { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
