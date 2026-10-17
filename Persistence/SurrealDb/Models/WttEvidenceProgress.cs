using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>CAS progress through one fixed-boundary, provider-bound historical evidence query.</summary>
[SurrealTable("wtt_evidence_progress", Aggregate = "Wtt", Guardrail = "G6 SCHEMAFULL")]
[Slice("wtt")]
public partial class WttEvidenceProgress : ISurrealRecord
{
    public const string SchemaNameConst = "wtt_evidence_progress";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string BindingJson { get; set; } = string.Empty;
    public string InitialStateJson { get; set; } = string.Empty;
    public string StateJson { get; set; } = string.Empty;
    public string? NextCursor { get; set; }
    public string SeenCursorHashesJson { get; set; } = "[]";
    public string Digest { get; set; } = string.Empty;
    public long Pages { get; set; }
    public long Version { get; set; }
    public bool Complete { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
