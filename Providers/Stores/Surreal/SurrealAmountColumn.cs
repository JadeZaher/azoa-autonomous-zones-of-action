// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// Reads a persisted base-unit amount column back into a <see cref="RailAmount"/>.
/// D13 made these columns strings of decimal base units precisely so no database type
/// re-imposes the 64-bit ceiling the domain type no longer has.
/// See <c>Persistence/SurrealDb/Models/AGENTS.md</c> §amount-columns.
/// </summary>
internal static class SurrealAmountColumn
{
    /// <summary>
    /// An absent column means "no amount recorded" and reads as zero. A PRESENT but
    /// unparseable value is corruption — a signed, blank, separated or non-numeric
    /// amount — and fails closed, because a row that cannot say how much it moved must
    /// not quietly read as having moved nothing.
    /// </summary>
    public static RailAmount Parse(string? value, string recordId, string what)
    {
        if (value is null)
            return RailAmount.Zero;

        if (!RailAmount.TryParse(value, ValueDimension.NativeChainAsset, out var amount))
            throw new InvalidDataException(
                $"{what} '{recordId}' contains an invalid persisted amount.");

        return amount;
    }
}
