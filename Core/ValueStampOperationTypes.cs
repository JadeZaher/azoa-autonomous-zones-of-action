// SPDX-License-Identifier: UNLICENSED
//
// §value-stamp. One declaration of "which operations move one identifiable quantity", because
// the wave that added IssueUnits to the WRITE side and not to the READ side shipped a row that
// answered "pricing": "Priced" beside "isStampable": false. Two lists that must stay identical
// are not a control; one list is.
//
// See Services/Analytics/AGENTS.md §value-stamp.

namespace AZOA.WebAPI.Core;

/// <summary>
/// The operation types that give up ONE identifiable leg and can therefore carry a value stamp.
/// </summary>
/// <remarks>
/// <para>The WRITE side is <c>BlockchainOperationManager.StampableAmountReaders</c>, a table
/// keyed by operation type that additionally knows WHICH parameter carries the quantity for each
/// one; the READ side is <c>AnalyticsReadService.IsStampable</c>, which needs only membership and
/// so derives it from <see cref="All"/> rather than restating it.</para>
/// <para>Both directions are held, and by the code rather than by prose. Declared-but-unreadable
/// is caught by <c>BlockchainOperationManager.BuildStampableAmountReaders</c>, which refuses to
/// initialise unless the table's keys are exactly this set — so a reader for a type that is not
/// declared here cannot ship either, which is the direction a switch statement could not hold at
/// all (a stray <c>case "Foo":</c> was enumerable by nothing).
/// <c>tests/AZOA.WebAPI.Tests/Analytics/StampableSetDriftTests.cs</c> asserts the same equality
/// so the disagreement fails the build rather than the boot, and asserts the read surface's
/// OUTPUT so membership is proved where a sponsor actually reads it.</para>
/// <para>Membership is NOT direction. A swap and an exchange move two assets, so they carry a
/// stamp but contribute nothing to a single-asset position; <c>AnalyticsReadService.Direction</c>
/// is the separate decision.</para>
/// </remarks>
public static class ValueStampOperationTypes
{
    /// <summary>Units created. The typed <c>Amount</c> field carries the count.</summary>
    public const string Mint = "Mint";

    /// <summary>Units destroyed. <c>Parameters["Amount"]</c> carries the count.</summary>
    public const string Burn = "Burn";

    /// <summary>Units moved out of the subject's own wallet.</summary>
    public const string Transfer = "Transfer";

    /// <summary>
    /// An allocation that pays units out of the reserve INTO the subject's account. Shares the
    /// transfer SHAPE (<c>Parameters["Amount"]</c> + <c>Parameters["SourceTokenId"]</c>) and not
    /// its direction.
    /// </summary>
    public const string IssueUnits = AzoaScopes.IssueUnitsOperationType;

    /// <summary>A reserve exchange. The stamp prices the leg that was GIVEN UP.</summary>
    public const string Exchange = "Exchange";

    /// <summary>A DEX swap. The stamp prices <c>AmountIn</c>.</summary>
    public const string Swap = "Swap";

    /// <summary>Every type above, as the one membership answer both sides read.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Mint,
        Burn,
        Transfer,
        IssueUnits,
        Exchange,
        Swap,
    };

    /// <summary>True when this operation type moves one identifiable quantity.</summary>
    public static bool Contains(string? operationType) =>
        operationType is not null && All.Contains(operationType);
}
