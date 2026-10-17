using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("exchange_purchase_allocation", Aggregate = "Fiat reversal purchase attribution", Guardrail = "One immutable lot-to-purchase identity whose recovered share only grows atomically")]
[Slice("identity")]
[Index("exchange_purchase_allocation_source", Fields = new[] { "source_id" })]
public sealed class ExchangePurchaseAllocation : ISurrealRecord
{
    public string SchemaName => "exchange_purchase_allocation";
    [Id] public string Id { get; set; } = string.Empty;
    public string LotId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string HoldRecordId { get; set; } = string.Empty;
    public string ConsumedSourceUnits { get; set; } = "0";
    public string RecoveredHoldUnits { get; set; } = "0";
    public string ClawedAssetUnits { get; set; } = "0";
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
