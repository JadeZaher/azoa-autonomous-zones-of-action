// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Fiat.Reversal;

/// <summary>What a person did with the units one fiat-funded issuance gave them.</summary>
public enum ReversedPurchaseKind
{
    /// <summary>
    /// The units are committed in an OPEN hold that has not been released. Unwinding it gives the
    /// commitment back to the reserve partition and moves nothing on any rail, because nothing
    /// ever left the holding account.
    /// </summary>
    OpenHold = 0,

    /// <summary>
    /// The units were exchanged for another asset the person now holds. Unwinding it takes that
    /// asset back with the reserve's clawback authority.
    /// </summary>
    IssuedAsset = 1,
}

/// <summary>
/// One thing an on-ramp lot's units turned into, and enough to unwind exactly that much of it.
/// </summary>
/// <param name="Kind">Which unwind applies.</param>
/// <param name="ChainType">Chain the claim lives on.</param>
/// <param name="SourceQuantityBaseUnits">
/// How many of the LOT's units this claim consumed. The ladder counts recovery in the reversed
/// asset's own units, so this is the figure a partial recovery is measured against.
/// </param>
/// <param name="HoldId">The hold to give back, on an <see cref="ReversedPurchaseKind.OpenHold"/> claim.</param>
/// <param name="AssetId">The asset to take back, on an <see cref="ReversedPurchaseKind.IssuedAsset"/> claim.</param>
/// <param name="AssetQuantityBaseUnits">How much of that asset to take back.</param>
/// <param name="HolderAddress">The account it is held in. Refused rather than guessed when absent.</param>
public sealed record ReversedPurchaseClaim(
    ReversedPurchaseKind Kind,
    string ChainType,
    BigInteger SourceQuantityBaseUnits,
    string? HoldId = null,
    string? AssetId = null,
    BigInteger AssetQuantityBaseUnits = default,
    string? HolderAddress = null);

/// <summary>
/// Answers "what did the units from THIS on-ramp lot become". It is the second rung of the
/// compensation ladder and the one piece of it whose data does not exist yet.
/// </summary>
/// <remarks>
/// The ladder can only unwind what it can attribute. Taking back an arbitrary hold a disputing
/// sponsor happens to benefit from would rob whichever HONEST backer actually funded it, so the
/// pipeline refuses to act without a per-lot link. That link is written by the sponsorship path
/// (Wave 5/6), not here.
/// </remarks>
public interface IReversedPurchaseProvenance
{
    /// <summary>
    /// What one lot's units were spent on, newest first. An empty list means "nothing is
    /// attributable", which the ladder treats as a definite answer, not as an error.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>> ListForLotAsync(
        string lotId, Guid subjectUserId, CancellationToken ct = default);

    /// <summary>
    /// False while no source of provenance is wired. The ladder records this on the reversal so
    /// an absorbed loss says WHY it could not be recovered instead of implying that the person
    /// had spent nothing.
    /// </summary>
    bool IsAvailable { get; }
}

/// <summary>
/// STUB (W4.3). The default provenance source: it records that nothing is attributable, and says
/// so with <see cref="IsAvailable"/> = false so no caller can mistake "no link exists yet" for
/// "this person spent nothing".
///
/// <para><b>What unblocks it.</b> A durable per-purchase link from an <c>on_ramp_lot</c> to what
/// its units bought — written by the sponsorship / project-unit path in Wave 5/6. When that
/// lands, register a real <see cref="IReversedPurchaseProvenance"/> ahead of this one and rung 2
/// of the ladder starts working with no other change: both unwinds
/// (<c>IPlatformHold.RefundAsync</c> and the reserve's asset clawback) are already built and
/// exercised by <c>ReversalTests</c>.</para>
///
/// <para>It fails CLOSED in the only direction that matters: with no provenance the ladder never
/// touches a hold or an asset it cannot attribute, and the shortfall becomes a recorded platform
/// loss plus a flagged account rather than a silent one.</para>
/// </summary>
public sealed class UnavailableReversedPurchaseProvenance : IReversedPurchaseProvenance
{
    /// <summary>The reason recorded on a reversal that fell through rung 2 for want of this.</summary>
    public const string UnavailableReason =
        "No record links this payment's units to what they were spent on, so nothing could be "
        + "unwound beyond the account balance itself.";

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public Task<AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>> ListForLotAsync(
        string lotId, Guid subjectUserId, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>.Success(
            Array.Empty<ReversedPurchaseClaim>(), UnavailableReason));
}
