// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// The seam a price oracle plugs into so a settled operation can be stamped with its
/// fiat value at transaction time (decision D16). <b>Nothing implements it yet</b> —
/// this codebase has no price source, and inventing one would put a fabricated number
/// on a financial record.
/// See <c>Persistence/SurrealDb/Models/AGENTS.md</c> §value-stamp for what a real
/// implementation owes its callers.
/// </summary>
public interface IValueRateSource
{
    /// <summary>
    /// The rate for <paramref name="dimension"/> at (or nearest before)
    /// <paramref name="asOf"/>, or <c>null</c> when this source cannot price it.
    /// </summary>
    /// <remarks>
    /// Returning <c>null</c> is a first-class answer and MUST be used whenever the
    /// source is unsure: the operation is then written with no stamp at all, which the
    /// analytics surface reads as "never priced" and which stays distinguishable from a
    /// genuine quote of zero. An implementation must never substitute a default, a last
    /// known value presented as current, or zero.
    /// </remarks>
    /// <param name="dimension">The unit being priced — the chain asset the amount counts.</param>
    /// <param name="chainType">The chain the operation settles on; two chains' "same" asset are not interchangeable.</param>
    /// <param name="asOf">The settlement instant the rate should describe.</param>
    ValueTask<ValueRateQuote?> TryQuoteAsync(
        ValueDimension dimension,
        string chainType,
        DateTimeOffset asOf,
        CancellationToken ct = default);
}

/// <summary>
/// The default rate source: it never quotes. Present so the stamping path has a real
/// collaborator to exercise and so "no oracle is wired" is an explicit, testable state
/// rather than a null check scattered through the manager.
/// </summary>
public sealed class UnpricedValueRateSource : IValueRateSource
{
    public static UnpricedValueRateSource Instance { get; } = new();

    public ValueTask<ValueRateQuote?> TryQuoteAsync(
        ValueDimension dimension,
        string chainType,
        DateTimeOffset asOf,
        CancellationToken ct = default)
        => ValueTask.FromResult<ValueRateQuote?>(null);
}
