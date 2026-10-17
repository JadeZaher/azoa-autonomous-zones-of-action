using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable operator attestation that one retired exchange operation's node-side ledger has been made good.</summary>
[SurrealTable("exchange_retired_resolution", Aggregate = "Exchange custody source",
    Guardrail = "G6 SCHEMAFULL; record id is the operation id, so one create-only resolution per retired exchange row")]
[SurrealNote("Generic reconciliation completes or fails a retired exchange row only when chain truth agrees with this attestation; see Services/Reserve/AGENTS.md §retired-rows.")]
[Slice("identity")]
public sealed class ExchangeRetiredResolution : ISurrealRecord
{
    public const string SchemaNameConst = "exchange_retired_resolution";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string OperationId { get; set; } = string.Empty;
    [Inside("Confirmed", "Failed")] public string Outcome { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string TransactionHash { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string Reason { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string ResolvedByUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
