using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Fill-once encrypted protocol key identity; only ciphertext wrapping can change by CAS.</summary>
[SurrealTable("wtt_stage_key", Aggregate = "Wtt", Guardrail = "G6 SCHEMAFULL")]
[Slice("wtt")]
public partial class WttStageKeyEnvelope : ISurrealRecord
{
    public const string SchemaNameConst = "wtt_stage_key";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string BindingJson { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string KeyVersion { get; set; } = string.Empty;
    public string EnvelopeJson { get; set; } = string.Empty;
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
