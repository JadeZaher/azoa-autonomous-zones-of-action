// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;

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
/// only two durable records that can say so — the person's own on-ramp lots and their own
/// exchange purchases.
/// </summary>
/// <remarks>
/// <para><b>Why it has to be derived.</b> Platform units are a fungible chain asset and the node
/// keeps no balance ledger of its own (D47), so nothing on chain can say which of a person's units
/// a card bought. <c>on_ramp_lot</c> is the only per-lot provenance there is, and the platform
/// already declares one rule for spending it: FIFO, oldest lot first. That is the rule the
/// source-aged cash-out consumes and it is the rule used here, so the two paths can never disagree
/// about which lot a unit came from.</para>
///
/// <para><b>What counts as a purchase.</b> Only an <c>Exchange</c> operation the SUBJECT made,
/// buying with the SAME asset the lot issued on the SAME chain. That is the only path in this node
/// where a person spends platform units (<c>Managers/ReserveExchangeManager.cs</c>); a hold opened
/// by an action node or by the hold controller is reserve-funded stake nobody paid for, and is
/// never attributed to anyone's card.</para>
///
/// <para><b>The robbery guard.</b> A hold is only ever claimed when the SUBJECT is its beneficiary
/// and nobody else is its funder — checked positively in <see cref="ClaimForHold"/>, never inferred
/// from how the hold was found. That is what keeps one person's chargeback off another person's
/// stake. Where a purchase's commitment cannot be identified UNIQUELY this source emits nothing for
/// it rather than guessing: the unrecovered units become a recorded platform loss, which is the
/// direction that hurts no honest backer.</para>
///
/// <para><b>Drift is loud.</b> The exchange records its legs as operation parameters, so a rename
/// on the producing side would silently stop attributing anything and turn every reversal into a
/// quiet platform loss. An <c>Exchange</c> row whose direction or spent-asset parameter is missing
/// is therefore an ERROR, not a skip: the ladder retries and then escalates to a person.</para>
/// </remarks>
public sealed class ExchangePurchaseProvenance : IReversedPurchaseProvenance
{
    /// <summary>Hold id the exchange opens a units leg under when the caller names none.</summary>
    /// <remarks>Mirrors <c>ReserveExchangeManager.CommitAsync</c>, which builds
    /// <c>"exchange:" + correlation</c> from the same correlation it stores as the operation's
    /// <c>IdempotencyKey</c>. This is the EXACT link between a purchase and its hold; everything
    /// below it in <see cref="ResolveClaim"/> is a shape match that is allowed to refuse.</remarks>
    public const string ExchangeHoldIdPrefix = "exchange:";

    // Parameter names written by Managers/ReserveExchangeManager.cs BuildOperation. Kept together
    // so the producer is one grep away, and treated as REQUIRED — see the drift note above.
    private const string DirectionParameter = "ExchangeDirection";
    private const string SpentAssetParameter = "SourceTokenId";
    private const string SpentAmountParameter = "Amount";
    private const string ReceivedAssetParameter = "TargetTokenId";
    private const string ReceivedAmountParameter = "AmountOut";
    private const string ChainParameter = "ChainType";
    private const string HolderAddressParameter = "WalletAddress";
    private const string PartitionParameter = "ReservePartition";
    private const string BuyDirection = "Buy";

    private const int OperationPageSize = 200;
    private const int MaxOperationPages = 25;

    /// <summary>How far either side of a purchase a caller-named hold may have been opened.</summary>
    /// <remarks>The hold is opened inside the exchange, before the operation row is written, so the
    /// window is lopsided on purpose. It narrows the match; it never widens it.</remarks>
    private static readonly TimeSpan HoldOpenedBefore = TimeSpan.FromHours(1);
    private static readonly TimeSpan HoldOpenedAfter = TimeSpan.FromMinutes(5);

    private readonly IOnRampLotLookup _lots;
    private readonly IBlockchainOperationStore _operations;
    private readonly IHoldLedgerStore _holds;
    private readonly ILogger<ExchangePurchaseProvenance> _logger;

    public ExchangePurchaseProvenance(
        IOnRampLotLookup lots,
        IBlockchainOperationStore operations,
        IHoldLedgerStore holds,
        ILogger<ExchangePurchaseProvenance> logger)
    {
        _lots = lots ?? throw new ArgumentNullException(nameof(lots));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _holds = holds ?? throw new ArgumentNullException(nameof(holds));
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

        // Only lots carrying an issuance instant take part: the FIFO frame is ordered on it, and a
        // lot without one cannot be placed against a purchase at all.
        var credited = (lotsRead.Result ?? Array.Empty<OnRampLot>())
            .Where(lot => lot.IssuedAt.HasValue)
            .ToList();

        var reversedLot = credited.FirstOrDefault(lot => SameRecord(lot.Id, lotId));
        if (reversedLot is null)
            return Nothing("This payment's issuance is not on the account's record of credited units.");

        // One asset, one chain. A lot that issued a DIFFERENT unit cannot compete for the same
        // purchases, and letting it consume their capacity would push this payment's own share off
        // the end of the walk and quietly under-recover.
        var frame = credited
            .Where(lot => SameAsset(lot.AssetId, reversedLot.AssetId)
                && SameChain(lot.ChainType, reversedLot.ChainType))
            .OrderBy(lot => lot.IssuedAt!.Value)
            .ThenBy(lot => lot.Id, StringComparer.Ordinal)
            .ToList();

        var purchases = await ReadPurchasesAsync(
            subjectUserId, frame[0].IssuedAt!.Value, reversedLot, ct);
        if (purchases.IsError)
            return AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>.Failure(purchases.Message);

        var attributed = AttributeFifo(frame, reversedLot, purchases.Result!);
        if (attributed.Count == 0)
            return Nothing("Nothing this account spent can be traced back to this payment.");

        var holdsRead = await _holds.ListForUserAsync(subjectUserId.ToString("N"), ct);
        if (holdsRead.IsError)
            return AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>.Failure(holdsRead.Message);
        var holds = holdsRead.Result ?? Array.Empty<HoldLedger>();

        var claims = new List<ReversedPurchaseClaim>();
        var alreadyClaimedHolds = new HashSet<string>(StringComparer.Ordinal);

        // Newest first: the interface's contract, and the order that unwinds the least settled
        // commitment before an older one the rest of the platform has already built on.
        foreach (var entry in attributed.OrderByDescending(candidate => candidate.Purchase.At))
        {
            var claim = ResolveClaim(
                entry.Purchase, entry.Units, holds, subjectUserId, alreadyClaimedHolds);
            if (claim is not null)
                claims.Add(claim);
        }

        return AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>.Success(claims, "Success");
    }

    // ── Reading what the person spent ─────────────────────────────────────────

    /// <summary>
    /// The subject's own exchange purchases of the lot's asset, oldest first. A page cap that is
    /// actually reached is an ERROR: attributing part of a history without saying so would
    /// under-recover silently.
    /// </summary>
    private async Task<AZOAResult<IReadOnlyList<ExchangePurchase>>> ReadPurchasesAsync(
        Guid subjectUserId, DateTimeOffset from, OnRampLot lot, CancellationToken ct)
    {
        var purchases = new List<ExchangePurchase>();

        for (var page = 0; page < MaxOperationPages; page++)
        {
            var read = await _operations.QueryAsync(
                new BlockchainOperationQuery
                {
                    UserId = subjectUserId,
                    From = from,
                    Skip = page * OperationPageSize,
                    Take = OperationPageSize,
                },
                ct);

            if (read.IsError)
                return AZOAResult<IReadOnlyList<ExchangePurchase>>.Failure(read.Message);

            var rows = read.Result ?? Array.Empty<IBlockchainOperation>();
            foreach (var row in rows)
            {
                var (purchase, drift) = Interpret(row, lot);
                if (drift is not null)
                    return AZOAResult<IReadOnlyList<ExchangePurchase>>.Failure(drift);
                if (purchase is not null)
                    purchases.Add(purchase);
            }

            if (rows.Count < OperationPageSize)
            {
                purchases.Sort((left, right) => left.At.CompareTo(right.At));
                return AZOAResult<IReadOnlyList<ExchangePurchase>>.Success(purchases, "Success");
            }
        }

        return AZOAResult<IReadOnlyList<ExchangePurchase>>.Failure(
            "This account has more history than the automatic attribution reads, so what the "
            + "payment funded has to be established by hand.");
    }

    /// <summary>One operation row read as a purchase, as irrelevant, or as producer drift.</summary>
    private static (ExchangePurchase? Purchase, string? Drift) Interpret(
        IBlockchainOperation row, OnRampLot lot)
    {
        if (!string.Equals(
                row.OperationType,
                AZOA.WebAPI.Managers.ReserveExchangeManager.ChainOperationType,
                StringComparison.Ordinal))
        {
            return (null, null);
        }

        // A failed exchange is compensated by the exchange itself — the hold is given back and the
        // partition returned — so it spent nothing. A broadcast-but-unconfirmed one DID draw.
        if (!string.Equals(row.Status, OperationStatus.Exchanged, StringComparison.Ordinal)
            && !string.Equals(row.Status, OperationStatus.PendingConfirmation, StringComparison.Ordinal))
        {
            return (null, null);
        }

        var parameters = row.Parameters;
        var direction = Parameter(parameters, DirectionParameter);
        var spentAssetId = Parameter(parameters, SpentAssetParameter);

        if (direction is null || spentAssetId is null)
            return (null, Drift(row, $"{DirectionParameter}/{SpentAssetParameter}"));

        if (!string.Equals(direction, BuyDirection, StringComparison.Ordinal))
            return (null, null);

        if (!string.Equals(spentAssetId, lot.AssetId?.Trim(), StringComparison.Ordinal))
            return (null, null);

        // The asset already matched, so this row IS about the reversed payment's unit: a missing
        // chain here is the producer drifting, not an exchange on some other chain.
        var chainType = Parameter(parameters, ChainParameter);
        if (chainType is null)
            return (null, Drift(row, ChainParameter));

        if (!string.Equals(chainType, lot.ChainType?.Trim(), StringComparison.OrdinalIgnoreCase))
            return (null, null);

        var spent = ReversalMath.ParseBaseUnits(Parameter(parameters, SpentAmountParameter));
        var receivedAssetId = Parameter(parameters, ReceivedAssetParameter);
        var received = ReversalMath.ParseBaseUnits(Parameter(parameters, ReceivedAmountParameter));

        if (spent <= BigInteger.Zero || received <= BigInteger.Zero || receivedAssetId is null)
        {
            return (null, Drift(
                row, $"{SpentAmountParameter}/{ReceivedAssetParameter}/{ReceivedAmountParameter}"));
        }

        return (
            new ExchangePurchase(
                Correlation: row.IdempotencyKey?.Trim(),
                At: Instant(row.CreatedDate),
                ChainType: chainType,
                SpentBaseUnits: spent,
                ReceivedAssetId: receivedAssetId,
                ReceivedBaseUnits: received,
                HolderAddress: Parameter(parameters, HolderAddressParameter),
                ReservePartition: Parameter(parameters, PartitionParameter)),
            null);
    }

    // ── Attribution ───────────────────────────────────────────────────────────

    /// <summary>
    /// Walks the person's purchases oldest-first and consumes their lots oldest-first, returning
    /// only what drew on the reversed one. Every lot in the frame consumes capacity, already
    /// reversed ones included: dropping them would shift later lots earlier and re-attribute a
    /// purchase that has already been unwound once.
    /// </summary>
    private static List<(ExchangePurchase Purchase, BigInteger Units)> AttributeFifo(
        IReadOnlyList<OnRampLot> frame,
        OnRampLot reversedLot,
        IReadOnlyList<ExchangePurchase> purchases)
    {
        // Units a cash-out or an earlier partial reversal already took cannot ALSO have bought
        // something, so the watermark comes off the capacity before anything is attributed.
        var capacity = new BigInteger[frame.Count];
        for (var index = 0; index < frame.Count; index++)
        {
            var available = ReversalMath.ParseBaseUnits(frame[index].QuantityBaseUnits)
                - ReversalMath.ParseBaseUnits(frame[index].ConsumedBaseUnits);
            capacity[index] = available > BigInteger.Zero ? available : BigInteger.Zero;
        }

        var attributed = new List<(ExchangePurchase Purchase, BigInteger Units)>();

        foreach (var purchase in purchases)
        {
            var unfunded = purchase.SpentBaseUnits;

            for (var index = 0; index < frame.Count && unfunded > BigInteger.Zero; index++)
            {
                // A lot cannot fund a purchase made before its units existed, and the frame is
                // ordered by that instant, so nothing after this one can either.
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

            // Whatever is left unfunded was paid for with units nobody put a card behind — earned
            // stake. It is not this reversal's to take back.
        }

        return attributed;
    }

    // ── Turning one attributed purchase into something the ladder can unwind ───

    private ReversedPurchaseClaim? ResolveClaim(
        ExchangePurchase purchase,
        BigInteger attributedUnits,
        IReadOnlyList<HoldLedger> holds,
        Guid subjectUserId,
        HashSet<string> alreadyClaimedHolds)
    {
        // 1. The exact link: the hold id the exchange derives from its own correlation.
        if (!string.IsNullOrWhiteSpace(purchase.Correlation))
        {
            var exact = holds.FirstOrDefault(hold =>
                SameChain(hold.Chain, purchase.ChainType)
                && string.Equals(
                    hold.HoldId?.Trim(),
                    ExchangeHoldIdPrefix + purchase.Correlation,
                    StringComparison.Ordinal));

            if (exact is not null)
                return ClaimForHold(exact, purchase, attributedUnits, subjectUserId, alreadyClaimedHolds);
        }

        // 2. A caller-named hold. Matched on the whole shape the exchange opened it with, and only
        //    when exactly ONE candidate exists — two matching commitments is an unanswerable
        //    question, and guessing at it is the robbery this pipeline exists to avoid.
        var candidates = holds.Where(hold => IsExchangeShaped(hold, purchase, subjectUserId)).ToList();

        if (candidates.Count == 1)
            return ClaimForHold(candidates[0], purchase, attributedUnits, subjectUserId, alreadyClaimedHolds);

        if (candidates.Count > 1)
        {
            _logger.LogWarning(
                "A reversed payment's purchase matches {Count} open commitments of {Asset} on "
                + "{Chain}; none was unwound, because unwinding the wrong one would take another "
                + "person's stake.",
                candidates.Count, purchase.ReceivedAssetId, purchase.ChainType);
            return null;
        }

        // 3. Nothing is held, so what the purchase produced went into the person's own account.
        //    Only the share this lot funded is ever taken back.
        if (string.IsNullOrWhiteSpace(purchase.HolderAddress))
            return null;

        var share = purchase.ReceivedBaseUnits * attributedUnits / purchase.SpentBaseUnits;
        if (share <= BigInteger.Zero)
            return null;

        return new ReversedPurchaseClaim(
            ReversedPurchaseKind.IssuedAsset,
            purchase.ChainType,
            attributedUnits,
            AssetId: purchase.ReceivedAssetId,
            AssetQuantityBaseUnits: share,
            HolderAddress: purchase.HolderAddress!.Trim());
    }

    /// <summary>
    /// The claim for one hold, or null. This is where the robbery guard actually runs: a hold is
    /// given back only when the reversed account is its beneficiary and nobody else funded it.
    /// </summary>
    private ReversedPurchaseClaim? ClaimForHold(
        HoldLedger hold,
        ExchangePurchase purchase,
        BigInteger attributedUnits,
        Guid subjectUserId,
        HashSet<string> alreadyClaimedHolds)
    {
        if (ReservePartitionScope.ParseRowUserId(hold.BeneficiaryUserId) != subjectUserId)
        {
            _logger.LogWarning(
                "A commitment matching a reversed payment is held for somebody else, so it was "
                + "left alone: taking it back would rob whoever it is for.");
            return null;
        }

        var funder = ReservePartitionScope.ParseRowUserId(hold.FunderUserId);
        if (funder != Guid.Empty && funder != subjectUserId)
            return null;

        if (!string.Equals(hold.AssetId?.Trim(), purchase.ReceivedAssetId, StringComparison.Ordinal))
            return null;

        if (!IsRefundable(hold.State))
            return null;

        var holdId = hold.HoldId?.Trim();
        if (string.IsNullOrWhiteSpace(holdId) || !alreadyClaimedHolds.Add(holdId!))
            return null;

        return new ReversedPurchaseClaim(
            ReversedPurchaseKind.OpenHold,
            string.IsNullOrWhiteSpace(hold.Chain) ? purchase.ChainType : hold.Chain.Trim(),
            attributedUnits,
            HoldId: holdId);
    }

    /// <summary>Every term the exchange opened a units leg with, and nothing looser.</summary>
    private static bool IsExchangeShaped(HoldLedger hold, ExchangePurchase purchase, Guid subjectUserId)
        => SameChain(hold.Chain, purchase.ChainType)
            && string.Equals(hold.AssetId?.Trim(), purchase.ReceivedAssetId, StringComparison.Ordinal)
            && ReversalMath.ParseBaseUnits(hold.Amount) == purchase.ReceivedBaseUnits
            && !string.IsNullOrWhiteSpace(purchase.ReservePartition)
            && string.Equals(
                hold.ReservePartition?.Trim(),
                purchase.ReservePartition!.Trim(),
                StringComparison.OrdinalIgnoreCase)
            && ReservePartitionScope.ParseRowUserId(hold.BeneficiaryUserId) == subjectUserId
            && IsRefundable(hold.State)
            && hold.CreatedAt >= purchase.At - HoldOpenedBefore
            && hold.CreatedAt <= purchase.At + HoldOpenedAfter;

    /// <summary>The two states a hold can still be given back from; mirrors <c>RefundAsync</c>.</summary>
    private static bool IsRefundable(string? state)
    {
        var trimmed = state?.Trim();
        return string.Equals(trimmed, nameof(HoldState.Pending), StringComparison.Ordinal)
            || string.Equals(trimmed, nameof(HoldState.Held), StringComparison.Ordinal);
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
        return leftTail.Length > 0
            && string.Equals(leftTail, Tail(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Parameter(IReadOnlyDictionary<string, string>? parameters, string name)
        => parameters is not null
            && parameters.TryGetValue(name, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;

    private static string Drift(IBlockchainOperation row, string parameterNames)
        => $"Exchange {row.Id} does not record {parameterNames}, so what this payment funded "
            + "cannot be read. Nothing was unwound.";

    private static DateTimeOffset Instant(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static AZOAResult<IReadOnlyList<ReversedPurchaseClaim>> Nothing(string reason)
        => AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>.Success(
            Array.Empty<ReversedPurchaseClaim>(), reason);

    /// <summary>One exchange purchase, reduced to the facts attribution needs.</summary>
    private sealed record ExchangePurchase(
        string? Correlation,
        DateTimeOffset At,
        string ChainType,
        BigInteger SpentBaseUnits,
        string ReceivedAssetId,
        BigInteger ReceivedBaseUnits,
        string? HolderAddress,
        string? ReservePartition);
}
