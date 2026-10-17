// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

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
/// <param name="SourceId">The settled exchange source the purchase was journaled under; the exact recovery link.</param>
public sealed record ReversedPurchaseClaim(
    ReversedPurchaseKind Kind,
    string ChainType,
    BigInteger SourceQuantityBaseUnits,
    string? HoldId = null,
    string? AssetId = null,
    BigInteger AssetQuantityBaseUnits = default,
    string? HolderAddress = null,
    string? SourceId = null);

/// <summary>
/// Answers "what did the units from THIS on-ramp lot become". It is the second rung of the
/// compensation ladder and the one piece of it whose data does not exist yet.
/// </summary>
/// <remarks>
/// The ladder can only unwind what it can attribute. Taking back an arbitrary hold a disputing
/// sponsor happens to benefit from would rob whichever HONEST backer actually funded it, so the
/// pipeline refuses to act without a per-lot link. <see cref="ExchangePurchaseProvenance"/> is
/// that link; <see cref="UnavailableReversedPurchaseProvenance"/> remains the fail-closed answer
/// for a node that has not registered one.
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
/// The refusing provenance source: it records that nothing is attributable, and says so with
/// <see cref="IsAvailable"/> = false so no caller can mistake "no link exists here" for "this
/// person spent nothing".
///
/// <para><b>No longer the default.</b> <see cref="ExchangePurchaseProvenance"/> is what
/// <c>ReversalRegistration.AddFiatReversalPipeline</c> wires, so rung 2 of the ladder attributes
/// and unwinds. This class stays for the node that deliberately refuses to attribute — it
/// registers this type itself, ahead of that call — and as the shape every provenance source has
/// to fail into: an empty list plus a reason, never an exception and never a silence.</para>
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

/// <summary>
/// The real per-lot link: what one fiat-funded issuance's units were spent on, derived from the
/// person's own on-ramp lots and their own settled exchange sources.
/// </summary>
/// <remarks>
/// Platform units are fungible and the node keeps no balance ledger (D47), so attribution walks the
/// lots and settled purchases oldest-first (FIFO) — the same rule the source-aged cash-out uses.
/// Each purchase is an immutable completed exchange source; a held purchase names its exact
/// liability, so no time-window or shape matching is ever used. See Services/Fiat/Reversal/AGENTS.md §provenance.
/// </remarks>
public sealed class ExchangePurchaseProvenance : IReversedPurchaseProvenance
{
    private const int MaxPurchases = 1000;

    private readonly IOnRampLotLookup _lots;
    private readonly IExchangeHoldSourceStore _sources;
    private readonly ILogger<ExchangePurchaseProvenance> _logger;

    public ExchangePurchaseProvenance(
        IOnRampLotLookup lots,
        IExchangeHoldSourceStore sources,
        ILogger<ExchangePurchaseProvenance> logger)
    {
        _lots = lots ?? throw new ArgumentNullException(nameof(lots));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>> ListForLotAsync(
        string lotId, Guid subjectUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lotId) || subjectUserId == Guid.Empty)
            return Nothing("This reversal does not name an issuance to attribute anything to.");

        var lotsRead = await _lots.ListForSubjectAsync(subjectUserId, ct);
        if (lotsRead.IsError)
            return AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>.Failure(lotsRead.Message);
        var credited = (lotsRead.Result ?? Array.Empty<OnRampLot>()).Where(lot => lot.IssuedAt.HasValue).ToList();
        var reversedLot = credited.FirstOrDefault(lot => SameRecord(lot.Id, lotId));
        if (reversedLot is null)
            return Nothing("This payment's issuance is not on the account's record of credited units.");

        // One asset, one chain: a lot of another unit cannot compete for the same purchases.
        var frame = credited
            .Where(lot => SameAsset(lot.AssetId, reversedLot.AssetId) && SameChain(lot.ChainType, reversedLot.ChainType))
            .OrderBy(lot => lot.IssuedAt!.Value).ThenBy(lot => lot.Id, StringComparer.Ordinal).ToList();

        var settled = await _sources.ListParticipantPurchasesAsync(subjectUserId, MaxPurchases, ct);
        if (settled.Count >= MaxPurchases)
            return AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>.Failure(
                "This account has more history than the automatic attribution reads, so what the payment funded has to be established by hand.");
        var purchases = settled.Select(source => Interpret(source, reversedLot)).OfType<ExchangePurchase>()
            .OrderBy(purchase => purchase.At).ThenBy(purchase => purchase.SourceId, StringComparer.Ordinal).ToList();
        var attributed = AttributeFifo(frame, reversedLot, purchases);
        if (attributed.Count == 0)
            return Nothing("Nothing this account spent can be traced back to this payment.");

        // Newest first: unwind the least settled commitment before older ones the platform has built on.
        var claims = new List<ReversedPurchaseClaim>();
        foreach (var (purchase, units) in attributed.OrderByDescending(entry => entry.Purchase.At))
        {
            var holder = purchase.HolderAddress;
            var deliverable = purchase.ReceivedBaseUnits;
            if (purchase.HoldRecordId is not null)
            {
                // A held purchase is recovered from custody until it is released; after release the units are the recipient's.
                var liability = await _sources.GetPurchaseLiabilityAsync(purchase.SourceId, ct);
                if (liability is not { State: "Released", ReleasedToAddress: { } releasedTo })
                {
                    claims.Add(new ReversedPurchaseClaim(ReversedPurchaseKind.OpenHold, purchase.ChainType, units,
                        HoldId: purchase.HoldId, SourceId: purchase.SourceId));
                    continue;
                }
                holder = releasedTo;
                deliverable = ReversalMath.ParseBaseUnits(liability.QuantityBaseUnits);
            }
            // §amounts: round up, capped at what was actually delivered.
            var share = BigInteger.Min(deliverable,
                (purchase.ReceivedBaseUnits * units + purchase.SpentBaseUnits - 1) / purchase.SpentBaseUnits);
            if (share <= BigInteger.Zero)
                continue;
            claims.Add(new ReversedPurchaseClaim(ReversedPurchaseKind.IssuedAsset, purchase.ChainType, units,
                AssetId: purchase.ReceivedAssetId, AssetQuantityBaseUnits: share, HolderAddress: holder,
                SourceId: purchase.SourceId));
        }
        _logger.LogDebug("Attributed {Count} settled purchases to reversed lot {LotId}.", claims.Count, lotId);
        return AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>.Success(claims, "Success");
    }

    /// <summary>One settled exchange source read as a purchase of the reversed lot's unit, or as irrelevant.</summary>
    private static ExchangePurchase? Interpret(ExchangeHoldSource source, OnRampLot lot)
    {
        ExchangeHoldSourceIntent? intent;
        try { intent = JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson); }
        catch (JsonException) { return null; }
        if (intent is null || intent.Units?.Kind == "Return" || !SameAsset(intent.PlatformAssetId, lot.AssetId)) return null;
        var asset = intent.Opening?.Asset ?? intent.Units?.Asset;
        var received = intent.Opening?.Definition?.QuantityBaseUnits ?? intent.Units?.QuantityBaseUnits;
        var spent = ReversalMath.ParseBaseUnits(intent.PlatformQuantityBaseUnits);
        if (asset is null || !SameChain(asset.ChainType, lot.ChainType) || spent <= BigInteger.Zero) return null;
        return new ExchangePurchase(source.Id, source.UpdatedAt, asset.ChainType, spent, asset.AssetId,
            ReversalMath.ParseBaseUnits(received), intent.ParticipantWallet.Address, source.HoldRecordId, intent.Opening?.HoldId);
    }

    /// <summary>
    /// Walks purchases oldest-first and consumes lots oldest-first, returning only what drew on the
    /// reversed lot. Only cash-out consumption comes off capacity; a reversal never moves a lot's place.
    /// </summary>
    private static List<(ExchangePurchase Purchase, BigInteger Units)> AttributeFifo(
        IReadOnlyList<OnRampLot> frame, OnRampLot reversedLot, IReadOnlyList<ExchangePurchase> purchases)
    {
        var capacity = new BigInteger[frame.Count];
        for (var index = 0; index < frame.Count; index++)
        {
            var cashedOut = ReversalMath.ParseBaseUnits(frame[index].ConsumedBaseUnits)
                - ReversalMath.ParseBaseUnits(frame[index].ReversedBaseUnits);
            var available = ReversalMath.ParseBaseUnits(frame[index].QuantityBaseUnits)
                - (cashedOut > BigInteger.Zero ? cashedOut : BigInteger.Zero);
            capacity[index] = available > BigInteger.Zero ? available : BigInteger.Zero;
        }
        var attributed = new List<(ExchangePurchase Purchase, BigInteger Units)>();
        foreach (var purchase in purchases)
        {
            var unfunded = purchase.SpentBaseUnits;
            for (var index = 0; index < frame.Count && unfunded > BigInteger.Zero; index++)
            {
                if (frame[index].IssuedAt!.Value > purchase.At)
                    break;
                if (capacity[index] <= BigInteger.Zero)
                    continue;
                var take = capacity[index] < unfunded ? capacity[index] : unfunded;
                capacity[index] -= take;
                unfunded -= take;
                if (ReferenceEquals(frame[index], reversedLot))
                    attributed.Add((purchase, take));
            }
        }
        return attributed;
    }

    private static bool SameChain(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameAsset(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);

    /// <summary>A record link keeps its <c>table:</c> prefix on read; compare the id itself.</summary>
    private static bool SameRecord(string? left, string? right)
    {
        static string Tail(string? value)
        {
            var text = value?.Trim() ?? string.Empty;
            var colon = text.LastIndexOf(':');
            return colon >= 0 && colon < text.Length - 1 ? text[(colon + 1)..] : text;
        }

        var leftTail = Tail(left);
        return leftTail.Length > 0 && string.Equals(leftTail, Tail(right), StringComparison.OrdinalIgnoreCase);
    }

    private static AZOAResult<IReadOnlyList<ReversedPurchaseClaim>> Nothing(string reason)
        => AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>.Success(Array.Empty<ReversedPurchaseClaim>(), reason);

    /// <summary>One settled purchase, reduced to the facts attribution needs.</summary>
    private sealed record ExchangePurchase(
        string SourceId,
        DateTimeOffset At,
        string ChainType,
        BigInteger SpentBaseUnits,
        string ReceivedAssetId,
        BigInteger ReceivedBaseUnits,
        string HolderAddress,
        string? HoldRecordId,
        string? HoldId);
}
