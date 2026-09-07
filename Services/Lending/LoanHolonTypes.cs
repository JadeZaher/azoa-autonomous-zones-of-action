// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Lending;

/// <summary>What one loan ledger entry records. D21 admits exactly these three kinds.</summary>
public enum LoanLedgerEntryKind
{
    /// <summary>Principal handed to the borrower; written once, by <c>LoanOpen</c>.</summary>
    Disbursement,

    /// <summary>Principal handed back; may be partial and may repeat.</summary>
    Repayment,

    /// <summary>Principal the lender abandons; ends the tracker.</summary>
    WriteOff,
}

/// <summary>
/// Lifecycle of a loan tracker. DERIVED from the outstanding BALANCE, never authored by a
/// tenant and never from the mere presence of a ledger row: a partial write-off against a
/// still-collectible principal leaves the tracker <see cref="Open"/> (D21 — partial
/// repayment and partial write-off are ordinary states).
/// </summary>
public enum LoanStatus
{
    /// <summary>Principal is still outstanding.</summary>
    Open,

    /// <summary>Outstanding reached zero, and nothing was abandoned to get there.</summary>
    Repaid,

    /// <summary>Outstanding reached zero and part of it was abandoned by the lender.</summary>
    WrittenOff,

    /// <summary>
    /// Repayments plus write-offs EXCEED the principal. Nothing a caller can ask for — the
    /// node refuses an over-repaying row — so it means two writers appended concurrently past
    /// the guard. Carried as its own state because flooring the excess into a clean
    /// <see cref="Repaid"/> would hide a ledger that disagrees with the value that moved.
    /// </summary>
    OverRepaid,
}

/// <summary>A tracker or ledger row that cannot say how much it moved, or in what unit.</summary>
public sealed class LoanLedgerFormatException : Exception
{
    public LoanLedgerFormatException(string message) : base(message) { }
}

/// <summary>
/// The persisted vocabulary of the principal-only loan tracker (D21): the two Holon
/// asset types, the metadata keys they carry, and the opt-in
/// <c>holon_type_registry</c> rows that constrain them.
/// See <c>Services/Actions/AGENTS.md</c> §loan-tracker.
/// </summary>
public static class LoanHolonTypes
{
    /// <summary>Asset type of the loan Holon itself — the tracker.</summary>
    public const string LoanTracker = "LoanTracker";

    /// <summary>Asset type of a child Holon of a tracker — one ledger row.</summary>
    public const string LoanLedgerEntry = "LoanLedgerEntry";

    /// <summary>Metadata keys written on tracker and ledger-entry Holons.</summary>
    public static class MetadataKeys
    {
        /// <summary>Tracker: the disbursed principal, a base-unit decimal string.</summary>
        public const string Principal = "principal";

        /// <summary>Ledger entry: the amount this row moved, a base-unit decimal string.</summary>
        public const string Amount = "amount";

        /// <summary>Both: <c>ChainAsset</c> or <c>FiatCurrency</c> — the <see cref="ValueDimensionKind"/> name.</summary>
        public const string CurrencyKind = "currencyKind";

        /// <summary>Both: the asset id (ASA/mint/contract) or the ISO-4217 code.</summary>
        public const string CurrencyCode = "currencyCode";

        /// <summary>Tracker: mirror of the DERIVED <see cref="LoanStatus"/>, for gate predicates.</summary>
        public const string Status = "status";

        /// <summary>Ledger entry: the <see cref="LoanLedgerEntryKind"/> name.</summary>
        public const string EntryKind = "entryKind";

        /// <summary>Ledger entry: the tracker it belongs to (mirrors the parent link).</summary>
        public const string LoanHolonId = "loanHolonId";

        /// <summary>Both: the tenant's own external reference. A SECONDARY replay key on a ledger entry.</summary>
        public const string Reference = "reference";

        /// <summary>
        /// Ledger entry: the <c>runId:nodeId</c> of the execution that appended it — the
        /// PRIMARY, always-present replay key. The tenant's <see cref="Reference"/> is
        /// optional, so it cannot be what protects an engine retry from double-counting.
        /// Same seed as the Bridge/Back/Swap handlers use for their idempotency keys.
        /// </summary>
        public const string ExecutionKey = "executionKey";

        /// <summary>Both: when the row was recorded, ISO-8601 round-trip.</summary>
        public const string RecordedAt = "recordedAt";

        /// <summary>Tracker: descriptive only — no authority is derived from it.</summary>
        public const string BorrowerUserId = "borrowerUserId";

        /// <summary>Tracker: descriptive only — no authority is derived from it.</summary>
        public const string LenderUserId = "lenderUserId";
    }

    /// <summary>The persisted spelling of a ledger entry kind.</summary>
    public static string ToText(LoanLedgerEntryKind kind) => kind.ToString();

    /// <summary>The persisted spelling of a tracker status.</summary>
    public static string ToText(LoanStatus status) => status.ToString();

    /// <summary>Parses a persisted or tenant-authored entry-kind spelling, case-insensitively.</summary>
    public static bool TryParseEntryKind(string? text, out LoanLedgerEntryKind kind) =>
        Enum.TryParse(text, ignoreCase: true, out kind) && Enum.IsDefined(kind);

    /// <summary>
    /// The opt-in <c>holon_type_registry</c> rows for this lane. Registering them makes
    /// <c>HolonManager.ValidateAssetTypeAsync</c> reject a tracker or ledger entry that
    /// omits any listed metadata key — the projection then cannot meet a row it is
    /// unable to read. Seeds are declarative: an operator upserts them through
    /// <c>HolonTypeRegistryController</c>; nothing in this lane self-registers.
    /// </summary>
    public static IReadOnlyList<HolonType> RegistrySeeds() => new[]
    {
        new HolonType
        {
            Id = LoanTracker,
            AssetType = LoanTracker,
            Description =
                "A principal-only loan tracker (D21). Carries the disbursed principal and its unit; "
                + "the repayment ledger is its child holons. No time-value field may be added to it.",
            IsActive = true,
            RequiredMetadataFields = new[]
            {
                MetadataKeys.Principal,
                MetadataKeys.CurrencyKind,
                MetadataKeys.CurrencyCode,
                MetadataKeys.Status,
            },
        },
        new HolonType
        {
            Id = LoanLedgerEntry,
            AssetType = LoanLedgerEntry,
            Description =
                "One row of a loan tracker's ledger: a disbursement, a repayment, or a write-off, "
                + "with the amount it moved and the unit that amount is counted in.",
            IsActive = true,
            RequiredMetadataFields = new[]
            {
                MetadataKeys.EntryKind,
                MetadataKeys.Amount,
                MetadataKeys.CurrencyKind,
                MetadataKeys.CurrencyCode,
                MetadataKeys.LoanHolonId,
            },
        },
    };
}

/// <summary>
/// Reads and writes <see cref="RailAmount"/> in Holon metadata as base-unit decimal
/// strings (<c>Persistence/SurrealDb/Models/AGENTS.md</c> §amount-columns, D13).
/// </summary>
/// <remarks>
/// Deliberate tightening of §amount-columns' "absent reads as zero": on a loan ledger an
/// absent amount is not "nothing moved", it is a row that cannot say what it did — so both
/// an absent and an unparseable value throw <see cref="LoanLedgerFormatException"/>.
/// </remarks>
public static class LoanAmountMetadata
{
    /// <summary>Writes <paramref name="amount"/> plus the two keys naming its unit.</summary>
    public static void Write(IDictionary<string, string> metadata, string amountKey, RailAmount amount)
    {
        metadata[amountKey] = amount.ToBaseUnitString();
        metadata[LoanHolonTypes.MetadataKeys.CurrencyKind] = amount.Dimension.Kind.ToString();
        metadata[LoanHolonTypes.MetadataKeys.CurrencyCode] = amount.Dimension.Code;
    }

    /// <summary>
    /// Builds the unit a loan is denominated in from tenant-authored config. The code is
    /// mandatory in this lane even for a chain asset: a ledger whose unit cannot be named
    /// cannot be reconciled, and the empty native-asset dimension would round-trip through
    /// metadata as an absent value.
    /// </summary>
    public static bool TryBuildDimension(string? kindText, string? code, out ValueDimension dimension, out string error)
    {
        dimension = default;
        error = string.Empty;

        var kind = ValueDimensionKind.ChainAsset;
        if (!string.IsNullOrWhiteSpace(kindText) && !Enum.TryParse(kindText, ignoreCase: true, out kind))
        {
            error = $"currencyKind '{kindText}' is not a known value dimension "
                + $"({nameof(ValueDimensionKind.ChainAsset)} or {nameof(ValueDimensionKind.FiatCurrency)}).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            error = "currencyCode is required: an amount whose unit is not named cannot be reconciled.";
            return false;
        }

        dimension = kind == ValueDimensionKind.FiatCurrency
            ? ValueDimension.Fiat(code)
            : ValueDimension.ChainAsset(code);
        return true;
    }

    /// <summary>The unit a tracker or ledger holon is denominated in, or throws.</summary>
    public static ValueDimension ReadDimension(IReadOnlyDictionary<string, string> metadata, Guid holonId)
    {
        metadata.TryGetValue(LoanHolonTypes.MetadataKeys.CurrencyKind, out var kindText);
        if (!Enum.TryParse<ValueDimensionKind>(kindText, ignoreCase: true, out var kind))
            throw new LoanLedgerFormatException(
                $"holon '{holonId}' has currencyKind '{kindText}', which is not a known value dimension.");

        if (!metadata.TryGetValue(LoanHolonTypes.MetadataKeys.CurrencyCode, out var code) || string.IsNullOrWhiteSpace(code))
            throw new LoanLedgerFormatException($"holon '{holonId}' does not name the unit its amount is counted in.");

        return kind == ValueDimensionKind.FiatCurrency
            ? ValueDimension.Fiat(code)
            : ValueDimension.ChainAsset(code);
    }

    /// <summary>
    /// The amount under <paramref name="amountKey"/>, in the holon's own unit. Throws on an
    /// absent or unparseable value — a row that cannot say how much it moved must never
    /// read as having moved nothing.
    /// </summary>
    public static RailAmount ReadRequired(IReadOnlyDictionary<string, string> metadata, string amountKey, Guid holonId)
    {
        var dimension = ReadDimension(metadata, holonId);

        if (!metadata.TryGetValue(amountKey, out var text) || string.IsNullOrWhiteSpace(text))
            throw new LoanLedgerFormatException($"holon '{holonId}' carries no '{amountKey}' value.");

        if (!RailAmount.TryParse(text, dimension, out var amount))
            throw new LoanLedgerFormatException(
                $"holon '{holonId}' has '{amountKey}' = '{text}', which is not a base-unit decimal string.");

        return amount;
    }
}

/// <summary>A tracker plus every ledger row the acting user may read for it.</summary>
public sealed record LoanLedgerSnapshot(IHolon Loan, IReadOnlyList<IHolon> Entries);

/// <summary>
/// The single runner-scoped read path for a loan tracker and its ledger. Both the
/// repayment recorder and the balance node go through it so the ownership rule is stated
/// once (same posture as D20 and the GateCheck holon resolver).
/// </summary>
public static class LoanLedgerReader
{
    /// <summary>
    /// One message for missing, unreadable, foreign-owned and wrong-typed trackers, so a
    /// caller cannot probe for the existence of another user's loan.
    /// </summary>
    public static string NotFound(Guid loanHolonId) => $"loan tracker '{loanHolonId}' not found or unreadable.";

    /// <summary>
    /// Loads the tracker owned by <paramref name="actingUserId"/> and its ledger entries,
    /// oldest first. Returns <c>(null, error)</c> and never partial data.
    /// </summary>
    public static async Task<(LoanLedgerSnapshot? Snapshot, string? Error)> TryLoadAsync(
        IHolonManager holonManager, Guid loanHolonId, Guid actingUserId)
    {
        // Runner-scoped read, then owner-STRICT: a public tracker owned by somebody else
        // is still refused — this lane never reads across users.
        var loanResult = await holonManager.GetAsync(loanHolonId, actingUserId);
        if (loanResult.IsError
            || loanResult.Result is not { } loan
            || loan.UserId != actingUserId
            || !string.Equals(loan.AssetType, LoanHolonTypes.LoanTracker, StringComparison.Ordinal))
            return (null, NotFound(loanHolonId));

        var childrenResult = await holonManager.GetChildrenAsync(loanHolonId, actingUserId);
        if (childrenResult.IsError)
            return (null, $"loan tracker '{loanHolonId}' ledger is unreadable: {childrenResult.Message}");

        var entries = new List<IHolon>();
        foreach (var child in childrenResult.Result ?? Enumerable.Empty<IHolon>())
        {
            if (!string.Equals(child.AssetType, LoanHolonTypes.LoanLedgerEntry, StringComparison.Ordinal))
                continue;

            // A ledger row we may not read would silently drop out of the sum and understate
            // the debt — refuse the whole projection instead.
            if (child.UserId != actingUserId)
                return (null,
                    $"loan tracker '{loanHolonId}' has ledger entry '{child.Id}' owned by another user; "
                    + "refusing to project a ledger that cannot be read whole.");

            entries.Add(child);
        }

        entries.Sort(static (left, right) =>
        {
            var byDate = left.CreatedDate.CompareTo(right.CreatedDate);
            return byDate != 0 ? byDate : left.Id.CompareTo(right.Id);
        });

        return (new LoanLedgerSnapshot(loan, entries), null);
    }
}
