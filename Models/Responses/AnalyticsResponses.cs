// SPDX-License-Identifier: UNLICENSED

using System.Text.Json.Serialization;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Whether a history entry carries a real fiat value stamp. Serialized on EVERY entry.
/// See <c>Services/Analytics/AGENTS.md</c> §unpriced-representation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PricingStatus
{
    /// <summary>No rate source ever quoted this operation. The absence is the fact, not a zero.</summary>
    NeverPriced = 0,

    /// <summary>A real rate source quoted this operation; <see cref="TransactionHistoryEntry.Value"/> is present.</summary>
    Priced = 1,
}

/// <summary>
/// Whether the log records a quantity for this entry at all. Serialized on EVERY entry, for
/// the same reason <see cref="PricingStatus"/> is: an absent figure that a consumer defaults
/// to zero is a fabricated quantity on a tax record.
/// See <c>Services/Analytics/AGENTS.md</c> §unknown-quantity.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuantityStatus
{
    /// <summary>The operation records no readable amount. The absence is the fact, not a zero.</summary>
    Unknown = 0,

    /// <summary>The operation records an amount, and <see cref="TransactionHistoryEntry.Amount"/> carries it.</summary>
    Known = 1,
}

/// <summary>The persisted <see cref="ValueStamp"/> as it crosses the wire — every component a string.</summary>
public sealed record ValueStampResponse
{
    /// <summary>ISO-4217 code of the stamped currency.</summary>
    public required string CurrencyCode { get; init; }

    /// <summary>The stamped value in <see cref="CurrencyCode"/>'s minor unit, as a decimal string.</summary>
    public required string MinorUnits { get; init; }

    /// <summary>The rate actually applied. <c>"0"</c> is a real quote of zero, not an absent one.</summary>
    public required string Rate { get; init; }

    /// <summary>When the applied rate was observed — not when it was applied.</summary>
    public required DateTimeOffset RateAsOf { get; init; }

    /// <summary>Provenance of the applied rate.</summary>
    public required string RateSource { get; init; }

    /// <summary>Projects a persisted stamp; the caller decides whether a null stamp is legal.</summary>
    public static ValueStampResponse From(ValueStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        return new ValueStampResponse
        {
            CurrencyCode = stamp.CurrencyCode,
            MinorUnits = stamp.MinorUnits,
            Rate = stamp.Rate,
            RateAsOf = stamp.RateAsOf,
            RateSource = stamp.RateSource,
        };
    }
}

/// <summary>
/// One row of the user's operation history, projected read-only from <c>operation_log</c>.
/// </summary>
/// <remarks>
/// <see cref="Pricing"/> is a REQUIRED non-nullable field carried alongside the nullable
/// <see cref="Value"/> on purpose: a JSON client with default null-stripping, or a
/// <c>.Value ?? 0</c> in a consumer's tax spreadsheet, turns an absent object into a silent
/// zero. An explicit <c>"pricing": "NeverPriced"</c> cannot be defaulted away.
/// </remarks>
public sealed record TransactionHistoryEntry
{
    /// <summary>The operation's durable id, so a consumer can re-fetch the full record.</summary>
    public required Guid OperationId { get; init; }

    /// <summary>When the operation was recorded (UTC).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>When the operation reached its terminal state, when it has.</summary>
    public DateTimeOffset? SettledAt { get; init; }

    /// <summary>Mint / Burn / Transfer / Swap / Exchange / DeployContract / CallContract / …</summary>
    public required string OperationType { get; init; }

    /// <summary>The <c>OperationStatus</c> constant this operation currently carries.</summary>
    public required string Status { get; init; }

    /// <summary>The asset class ("NFT", "Token"), when the operation recorded one.</summary>
    public string? AssetType { get; init; }

    /// <summary>The chain-side asset identifier (ASA id, SPL mint, contract), when recorded.</summary>
    public string? AssetId { get; init; }

    /// <summary>The chain the operation ran against, when recorded.</summary>
    public string? ChainType { get; init; }

    /// <summary>
    /// Base-unit decimal string — never a JSON number, which would truncate wei-scale
    /// amounts. NULL when the operation records no readable quantity; read it together with
    /// <see cref="AmountStatus"/>, never as <c>Amount ?? "0"</c>.
    /// </summary>
    public string? Amount { get; init; }

    /// <summary>
    /// Always serialized. <see cref="QuantityStatus.Unknown"/> iff <see cref="Amount"/> is
    /// null — the quantity-axis twin of <see cref="Pricing"/>, so a null-stripping client
    /// cannot turn "the log does not say" into a real zero.
    /// </summary>
    public required QuantityStatus AmountStatus { get; init; }

    /// <summary>The other side of the movement (a transfer recipient), when recorded.</summary>
    public string? CounterpartyAddress { get; init; }

    /// <summary>The on-chain transaction hash, when one was broadcast.</summary>
    public string? TxHash { get; init; }

    /// <summary>The node fee charged on this operation, base-unit decimal string, when one was.</summary>
    public string? Fee { get; init; }

    /// <summary>
    /// Whether this operation is the KIND of operation that can ever carry a value stamp.
    /// False for Swap/Exchange (two assets, no single quantity), DeployContract/CallContract
    /// (no quantity), and any non-broadcast status. A consumer must not read an unstampable
    /// row as a pricing gap.
    /// </summary>
    public required bool IsStampable { get; init; }

    /// <summary>Always serialized. <see cref="PricingStatus.NeverPriced"/> iff <see cref="Value"/> is null.</summary>
    public required PricingStatus Pricing { get; init; }

    /// <summary>The frozen fiat value; null iff <see cref="Pricing"/> is <see cref="PricingStatus.NeverPriced"/>.</summary>
    public ValueStampResponse? Value { get; init; }
}

/// <summary>One page of <see cref="TransactionHistoryEntry"/> rows, newest first.</summary>
public sealed record TransactionHistoryPage
{
    public required IReadOnlyList<TransactionHistoryEntry> Items { get; init; }

    /// <summary>The offset this page started at.</summary>
    public required int Skip { get; init; }

    /// <summary>The page size actually applied after clamping.</summary>
    public required int Take { get; init; }

    /// <summary>True when a full page came back, so another page may exist.</summary>
    public required bool HasMore { get; init; }

    /// <summary>Rows in this page that carry no value stamp — the pricing gap, stated up front.</summary>
    public required int UnpricedEntryCount { get; init; }

    /// <summary>Rows in this page whose quantity the log does not record — the same gap on the quantity axis.</summary>
    public required int UnknownAmountEntryCount { get; init; }
}

/// <summary>A fiat total. Present only when every contributing entry was really priced.</summary>
public sealed record FiatValueResponse
{
    public required string CurrencyCode { get; init; }

    /// <summary>Signed decimal string in the currency's minor unit.</summary>
    public required string MinorUnits { get; init; }
}

/// <summary>
/// The user's net movement in one (chain, asset) pair over the queried window, with the
/// completeness of both the quantity and the value stated explicitly.
/// </summary>
/// <remarks>
/// An unpriced leg is NEVER summed as zero. A fiat total that silently omits unpriced legs
/// is a wrong number that looks right, and this is a tax-workflow surface — so a total is
/// either complete or absent. See <c>Services/Analytics/AGENTS.md</c> §unpriced-representation.
/// </remarks>
public sealed record PositionSummary
{
    /// <summary>The chain, when the operations recorded one.</summary>
    public string? ChainType { get; init; }

    /// <summary>The chain-side asset identifier; null means the chain's own native asset.</summary>
    public string? AssetId { get; init; }

    /// <summary>
    /// Net base units moved by the SETTLED, direction-classified entries — a signed decimal
    /// string, because a burn or an outbound transfer moves the position down.
    /// </summary>
    public required string Quantity { get; init; }

    /// <summary>Every entry that grouped into this position, whatever its status.</summary>
    public required int EntryCount { get; init; }

    /// <summary>Entries excluded from <see cref="Quantity"/> because nothing has settled yet.</summary>
    public required int UnsettledEntryCount { get; init; }

    /// <summary>
    /// Entries excluded from <see cref="Quantity"/> because the log does not determine which
    /// way they moved (Swap/Exchange move two assets; Deploy/Call move none).
    /// </summary>
    public required int UnclassifiedEntryCount { get; init; }

    /// <summary>
    /// Entries excluded from <see cref="Quantity"/> because the log records no readable
    /// amount for them. Counted rather than folded in as zero: a zero is a movement of
    /// nothing, and "the log does not say" is not that.
    /// </summary>
    public required int UnknownAmountEntryCount { get; init; }

    /// <summary>
    /// True iff every entry settled, every entry's direction is known, and every entry
    /// stated its quantity.
    /// </summary>
    public required bool IsQuantityComplete { get; init; }

    /// <summary>Entries carrying a real value stamp.</summary>
    public required int PricedEntryCount { get; init; }

    /// <summary>Entries carrying no value stamp at all, for any reason.</summary>
    public required int UnpricedEntryCount { get; init; }

    /// <summary>
    /// The subset of <see cref="UnpricedEntryCount"/> that can NEVER be stamped by design.
    /// Carried separately so an unstampable Swap is not misread as a missing price.
    /// </summary>
    public required int UnstampableEntryCount { get; init; }

    /// <summary>True iff <see cref="UnpricedEntryCount"/> is zero.</summary>
    public required bool IsValueComplete { get; init; }

    /// <summary>
    /// Net stamped fiat value of the settled movements. Null unless the position is both
    /// value- and quantity-complete and every stamp agrees on one currency — which is the
    /// truthful answer today, because nothing implements <c>IValueRateSource</c>.
    /// </summary>
    public FiatValueResponse? RealizedValue { get; init; }

    /// <summary>
    /// Current fiat value of the holding. Always null: it needs a CURRENT quote, and no rate
    /// source exists. A stamp is a historical observation — presenting one as today's price
    /// is precisely what §value-stamp forbids.
    /// </summary>
    public FiatValueResponse? UnrealizedValue { get; init; }
}

/// <summary>Positions over the queried window, plus whether the scan that built them was complete.</summary>
public sealed record PositionsResponse
{
    public required IReadOnlyList<PositionSummary> Items { get; init; }

    /// <summary>Operations folded into <see cref="Items"/>.</summary>
    public required int ScannedEntryCount { get; init; }

    /// <summary>
    /// False when the scan hit its bound before exhausting the window, so the positions cover
    /// only part of it. A truncated total is stated as truncated, never rendered as final.
    /// </summary>
    public required bool IsComplete { get; init; }
}
