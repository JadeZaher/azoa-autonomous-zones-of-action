// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Services.Lending;

/// <summary>The projected state of one loan tracker, in <see cref="RailAmount"/> terms.</summary>
/// <param name="Outstanding">
/// What is still collectible, floored at zero so no row can express a negative debt.
/// </param>
/// <param name="Excess">
/// How far repayments plus write-offs OVERSHOT the principal — the amount the floor on
/// <paramref name="Outstanding"/> removed. Non-zero only alongside
/// <see cref="LoanStatus.OverRepaid"/>, and carried so the floor hides nothing.
/// </param>
public sealed record LoanBalanceTotals(
    RailAmount Principal,
    RailAmount TotalRepaid,
    RailAmount TotalWrittenOff,
    RailAmount Outstanding,
    LoanStatus Status,
    int EntryCount,
    RailAmount Excess = default);

/// <summary>
/// The node output of every loan-tracker node. Every amount is a base-unit decimal
/// STRING (D13): a JSON number would silently lose precision above 2^53.
/// </summary>
/// <remarks>
/// The JSON names are pinned with <see cref="JsonPropertyNameAttribute"/> rather than
/// inherited from the serializer, so this output stays camelCase regardless of the
/// options a caller uses. Note this is the one deliberate exception to
/// <c>Services/Actions/AGENTS.md</c> §output-schema's "field names are PascalCase"
/// observation — the catalog entry for these three node types must use these names.
/// </remarks>
public sealed record LoanBalanceProjectionResult
{
    [JsonPropertyName("loanId")]
    public string LoanId { get; init; } = string.Empty;

    [JsonPropertyName("principal")]
    public string Principal { get; init; } = "0";

    [JsonPropertyName("totalRepaid")]
    public string TotalRepaid { get; init; } = "0";

    [JsonPropertyName("totalWrittenOff")]
    public string TotalWrittenOff { get; init; } = "0";

    [JsonPropertyName("outstanding")]
    public string Outstanding { get; init; } = "0";

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("entryCount")]
    public int EntryCount { get; init; }
}

/// <summary>
/// The one place a loan balance is computed (D21): a pure fold over a tracker holon and
/// its ledger-entry children. No IO, no clock, no chain — the callers hand it holons they
/// have already read runner-scoped. See <c>Services/Actions/AGENTS.md</c> §loan-tracker.
/// </summary>
/// <remarks>
/// <para><c>outstanding = principal − Σ repayments − Σ write-offs</c>, computed in
/// <see cref="BigInteger"/> and floored at zero so a bad row can never express a negative
/// debt. Disbursement rows record the fact that principal was handed over — the tracker's
/// own <c>principal</c> is authoritative, so they do not re-add to the total.</para>
/// <para><b>Status follows the BALANCE, never the presence of a row.</b> While anything is
/// still collectible the tracker is <see cref="LoanStatus.Open"/>, whether or not part of it
/// has been written off — D21 makes partial repayment and partial write-off ordinary states,
/// and a 100-unit write-off against 1000 outstanding must not read as terminally
/// <see cref="LoanStatus.WrittenOff"/> while 900 is still collectible. At zero the tracker is
/// <see cref="LoanStatus.WrittenOff"/> when anything was abandoned to get there and
/// <see cref="LoanStatus.Repaid"/> when it was not.</para>
/// <para>The floor is a guard, not an eraser: when the ledger returns MORE than the
/// principal the status is <see cref="LoanStatus.OverRepaid"/> and
/// <see cref="LoanBalanceTotals.Excess"/> carries the overshoot, so the inconsistency is
/// reported rather than smoothed into a clean <see cref="LoanStatus.Repaid"/>.</para>
/// <para>Every entry must be denominated in the tracker's unit. A mixed-unit ledger throws
/// <see cref="LoanLedgerFormatException"/> rather than summing an ALGO repayment into a
/// USDC principal.</para>
/// </remarks>
public static class LoanBalanceProjection
{
    /// <summary>Folds the ledger into totals. Throws <see cref="LoanLedgerFormatException"/> on an unreadable row.</summary>
    public static LoanBalanceTotals Compute(IHolon loan, IEnumerable<IHolon> ledgerEntries)
    {
        var principal = LoanAmountMetadata.ReadRequired(
            loan.Metadata, LoanHolonTypes.MetadataKeys.Principal, loan.Id);
        var dimension = principal.Dimension;

        var repaid = BigInteger.Zero;
        var writtenOff = BigInteger.Zero;
        var entryCount = 0;

        foreach (var entry in ledgerEntries)
        {
            if (!string.Equals(entry.AssetType, LoanHolonTypes.LoanLedgerEntry, StringComparison.Ordinal))
                continue;

            entryCount++;

            entry.Metadata.TryGetValue(LoanHolonTypes.MetadataKeys.EntryKind, out var kindText);
            if (!LoanHolonTypes.TryParseEntryKind(kindText, out var kind))
                throw new LoanLedgerFormatException(
                    $"ledger entry '{entry.Id}' has entryKind '{kindText}', which is not a known kind.");

            var amount = LoanAmountMetadata.ReadRequired(
                entry.Metadata, LoanHolonTypes.MetadataKeys.Amount, entry.Id);

            if (amount.Dimension != dimension)
                throw new LoanLedgerFormatException(
                    $"ledger entry '{entry.Id}' is denominated in {amount.Dimension} but the loan principal "
                    + $"is in {dimension}; a mixed-unit ledger cannot be summed.");

            switch (kind)
            {
                case LoanLedgerEntryKind.Repayment:
                    repaid += amount.BaseUnits;
                    break;
                case LoanLedgerEntryKind.WriteOff:
                    writtenOff += amount.BaseUnits;
                    break;
                case LoanLedgerEntryKind.Disbursement:
                    // Recorded fact only: the tracker's principal is the authoritative figure.
                    break;
            }
        }

        // Signed on purpose: the sign is the only place an over-repaid ledger announces
        // itself, and it is read BEFORE the floor is applied.
        var balance = principal.BaseUnits - repaid - writtenOff;
        var outstanding = balance.Sign < 0 ? BigInteger.Zero : balance;
        var excess = balance.Sign < 0 ? -balance : BigInteger.Zero;

        var status = !outstanding.IsZero
            ? LoanStatus.Open
            : !excess.IsZero
                ? LoanStatus.OverRepaid
                : writtenOff.IsZero ? LoanStatus.Repaid : LoanStatus.WrittenOff;

        return new LoanBalanceTotals(
            principal,
            RailAmount.In(dimension, repaid),
            RailAmount.In(dimension, writtenOff),
            RailAmount.In(dimension, outstanding),
            status,
            entryCount,
            RailAmount.In(dimension, excess));
    }

    /// <summary>Folds the ledger and renders the node output DTO.</summary>
    public static LoanBalanceProjectionResult Project(IHolon loan, IEnumerable<IHolon> ledgerEntries) =>
        ToResult(loan.Id, Compute(loan, ledgerEntries));

    /// <summary>Renders already-computed totals as the node output DTO.</summary>
    public static LoanBalanceProjectionResult ToResult(Guid loanId, LoanBalanceTotals totals) => new()
    {
        LoanId = loanId.ToString(),
        Principal = totals.Principal.ToBaseUnitString(),
        TotalRepaid = totals.TotalRepaid.ToBaseUnitString(),
        TotalWrittenOff = totals.TotalWrittenOff.ToBaseUnitString(),
        Outstanding = totals.Outstanding.ToBaseUnitString(),
        Status = LoanHolonTypes.ToText(totals.Status),
        EntryCount = totals.EntryCount,
    };
}
