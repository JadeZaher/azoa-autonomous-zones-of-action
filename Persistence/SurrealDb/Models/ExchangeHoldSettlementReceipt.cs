using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("exchange_hold_settlement_receipt", Aggregate = "Exchange finalized accounting", Guardrail = "Exact source, wire and custody projection commit once")]
[Slice("identity")]
public sealed class ExchangeHoldSettlementReceipt : ISurrealRecord
{
    public string SchemaName => "exchange_hold_settlement_receipt";
    [Id] public string Id { get; set; } = string.Empty;
    public string SourceIntentHash { get; set; } = string.Empty;
    public string PreparedHash { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = string.Empty;
    public string HoldRecordId { get; set; } = string.Empty;
    public string TreasuryRecordId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
