// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Services.Fiat.Reversal;

/// <summary>What one step of the compensation ladder concluded.</summary>
/// <param name="Row">The reversal as it now stands.</param>
/// <param name="Definite">
/// True when the node reached an answer it will not change by trying again. False means the
/// outcome could not be established (an unreadable rail, a store that would not write), which is
/// the only case the saga may spend a retry on.
/// </param>
/// <param name="IndeterminateReason">Why the answer is not definite yet. Null when it is.</param>
public sealed record ReversalStepOutcome(FiatReversal Row, bool Definite, string? IndeterminateReason);

/// <summary>
/// The D10 compensation ladder: what the node does when money it has already turned into units
/// is taken back. See <c>Services/Fiat/Reversal/AGENTS.md</c> §ladder.
/// </summary>
public interface IReversalCompensation
{
    /// <summary>
    /// Runs rungs 1 and 2 — the units themselves, then what they bought — and records exactly
    /// how much each recovered. Never absorbs: that is the next step's decision.
    /// </summary>
    Task<AZOAResult<ReversalStepOutcome>> RecoverAsync(string reversalId, CancellationToken ct = default);

    /// <summary>
    /// Rung 3. Closes the reversal: whatever is still outstanding becomes a recorded platform
    /// loss and, when the flow's policy says so, a flag on the account.
    /// </summary>
    Task<AZOAResult<ReversalStepOutcome>> SettleShortfallAsync(
        string reversalId, CancellationToken ct = default);

    /// <summary>
    /// Parks a reversal at a status only a person can move it off, with the reason on the row.
    /// The last-resort record when even absorbing could not be written down.
    /// </summary>
    Task<AZOAResult<FiatReversal>> ParkForOperatorAsync(
        string reversalId, string reason, CancellationToken ct = default);
}

/// <summary>
/// Exact integer arithmetic for reversals. Kept total and side-effect free so a partial refund's
/// proportion can be re-derived from the row years later.
/// </summary>
public static class ReversalMath
{
    /// <summary>Parses a stored base-unit figure; a blank or unreadable value is zero.</summary>
    public static BigInteger ParseBaseUnits(string? raw)
        => BigInteger.TryParse(
            raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : BigInteger.Zero;

    /// <summary>Formats a base-unit figure the way every other value row stores one.</summary>
    public static string Format(BigInteger value)
        => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// How many units a reversal of <paramref name="reversedMinorUnits"/> out of
    /// <paramref name="sourceMinorUnits"/> has to take back, given that the payment issued
    /// <paramref name="issuedBaseUnits"/>.
    /// </summary>
    /// <remarks>
    /// Rounds UP and then caps at what was issued. Rounding down would leave the platform a
    /// fraction of a unit out of pocket on every partial reversal; the cap is what stops rounding
    /// up from ever taking back more than the payment bought.
    /// </remarks>
    public static BigInteger ProportionalTarget(
        BigInteger issuedBaseUnits, long reversedMinorUnits, long sourceMinorUnits)
    {
        if (issuedBaseUnits <= BigInteger.Zero || reversedMinorUnits <= 0)
            return BigInteger.Zero;

        if (sourceMinorUnits <= 0 || reversedMinorUnits >= sourceMinorUnits)
            return issuedBaseUnits;

        var numerator = issuedBaseUnits * reversedMinorUnits;
        var denominator = new BigInteger(sourceMinorUnits);
        var ceiling = (numerator + denominator - BigInteger.One) / denominator;

        return ceiling > issuedBaseUnits ? issuedBaseUnits : ceiling;
    }
}

/// <summary>
/// Takes back what a reversed payment bought, in the order that hurts the fewest honest people:
/// the payer's own units first, then only what can be ATTRIBUTED to this payment, then the
/// platform's own pocket.
///
/// <para>Every value move here is claimed on <see cref="IIdempotencyStore"/> before it is
/// attempted, so a redelivered webhook or a retried saga step replays a recovery instead of
/// performing a second one — and a move whose outcome could not be established parks the reversal
/// for a person rather than being tried again blind.</para>
/// </summary>
public sealed class ReversalCompensationService : IReversalCompensation
{
    /// <summary>Idempotency operation type stamped on every recovery claim.</summary>
    public const string RecoveryOperationType = "fiat-reversal-recovery";

    /// <summary>The reason recorded when a value move's outcome could not be established.</summary>
    public const string AmbiguousRecoveryReason =
        "A recovery move was attempted and its outcome could not be established. Confirm it "
        + "against the rail before retrying: repeating it blind could take back more than the "
        + "reversal is owed.";

    private const int MaxReasonLength = 512;

    private readonly IFiatReversalLedger _reversals;
    private readonly IOnRampLotLedger _lots;
    private readonly IPlatformReserve _reserve;
    private readonly IPlatformHold _holds;
    private readonly IReversedPurchaseProvenance _provenance;
    private readonly IBlockchainProviderFactory _providerFactory;
    private readonly IWalletStore _wallets;
    private readonly IIdempotencyStore _idempotency;
    private readonly ReversalOptions _options;
    private readonly OnRampOptions _onRamp;
    private readonly ILogger<ReversalCompensationService> _logger;
    private readonly TimeProvider _clock;

    public ReversalCompensationService(
        IFiatReversalLedger reversals,
        IOnRampLotLedger lots,
        IPlatformReserve reserve,
        IPlatformHold holds,
        IReversedPurchaseProvenance provenance,
        IBlockchainProviderFactory providerFactory,
        IWalletStore wallets,
        IIdempotencyStore idempotency,
        ReversalOptions options,
        OnRampOptions onRamp,
        ILogger<ReversalCompensationService> logger,
        TimeProvider? clock = null)
    {
        _reversals = reversals ?? throw new ArgumentNullException(nameof(reversals));
        _lots = lots ?? throw new ArgumentNullException(nameof(lots));
        _reserve = reserve ?? throw new ArgumentNullException(nameof(reserve));
        _holds = holds ?? throw new ArgumentNullException(nameof(holds));
        _provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _wallets = wallets ?? throw new ArgumentNullException(nameof(wallets));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _onRamp = onRamp ?? throw new ArgumentNullException(nameof(onRamp));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ReversalStepOutcome>> RecoverAsync(
        string reversalId, CancellationToken ct = default)
    {
        var read = await _reversals.GetAsync(reversalId, ct);
        if (read.IsError)
            return AZOAResult<ReversalStepOutcome>.Failure(read.Message);
        if (read.Result is not { } row)
            return AZOAResult<ReversalStepOutcome>.Failure($"Reversal '{reversalId}' was not found.");

        // A reversal a person now owns, or one already closed, is not the ladder's business.
        if (FiatReversalStatuses.IsSettled(row.Status) || FiatReversalStatuses.NeedsPerson(row.Status))
            return Definite(row);

        var policy = _options.PolicyFor(row.FlowPurpose);

        // A dispute nobody has debited yet. The flag is the proportionate response; taking units
        // back over a question that may be answered in the node's favour is not.
        if (string.Equals(row.Kind, FiatReversalKinds.Dispute, StringComparison.Ordinal)
            && !row.FundsWithdrawn
            && !policy.RecoverOnDisputeOpened)
        {
            return Definite(row);
        }

        var target = ReversalMath.ParseBaseUnits(row.TargetQuantityBaseUnits);
        var recovered = ReversalMath.ParseBaseUnits(row.RecoveredQuantityBaseUnits);
        var outstanding = target - recovered;
        if (outstanding <= BigInteger.Zero)
            return Definite(row);

        // A reversal small enough that chasing it costs more than it returns. Explicit, operator
        // set, and recorded on the row -- the difference between a policy and an accident.
        if (policy.AbsorbAtOrBelowMinorUnits > 0
            && row.AmountMinorUnits <= policy.AbsorbAtOrBelowMinorUnits)
        {
            row.StatusReason = Truncate(
                "The reversal is at or below the amount this flow absorbs without touching the "
                + "account.", MaxReasonLength);
            var parked = await SaveAsync(row, ct);
            return parked.IsError
                ? Indeterminate(row, parked.Message)
                : Definite(parked.Result!);
        }

        var recoveredThisPass = BigInteger.Zero;
        string? path = null;
        string? transactionId = null;
        string? refusal = null;

        // ── Rung 1: the units are still in the payer's own account ──
        if (policy.RecoverFromBalance && outstanding > BigInteger.Zero)
        {
            var balance = await RecoverFromBalanceAsync(row, outstanding, ct);
            if (balance.Ambiguous)
                return await ParkAmbiguousAsync(row, balance.Detail, ct);
            if (!balance.Definite)
                return Indeterminate(row, balance.Detail);

            if (balance.Amount > BigInteger.Zero)
            {
                recoveredThisPass += balance.Amount;
                outstanding -= balance.Amount;
                path = FiatReversalRecoveryPaths.Combine(path, FiatReversalRecoveryPaths.Balance);
                transactionId ??= balance.TransactionId;
            }
            else
            {
                refusal ??= balance.Detail;
            }
        }

        // ── Rung 2: what those units bought, and ONLY what can be attributed to this payment ──
        if (outstanding > BigInteger.Zero
            && (policy.RecoverFromOpenHolds || policy.RecoverFromPurchasedUnits))
        {
            var purchases = await RecoverFromPurchasesAsync(row, policy, outstanding, ct);
            if (purchases.Ambiguous)
                return await ParkAmbiguousAsync(row, purchases.Detail, ct);
            if (!purchases.Definite)
                return Indeterminate(row, purchases.Detail);

            if (purchases.Amount > BigInteger.Zero)
            {
                recoveredThisPass += purchases.Amount;
                outstanding -= purchases.Amount;
                path = FiatReversalRecoveryPaths.Combine(
                    path, purchases.Path ?? FiatReversalRecoveryPaths.Purchase);
                transactionId ??= purchases.TransactionId;
            }
            else
            {
                refusal ??= purchases.Detail;
            }
        }

        row.RecoveredQuantityBaseUnits = ReversalMath.Format(recovered + recoveredThisPass);
        row.AttemptCount += 1;
        if (path is not null)
            row.RecoveryPath = FiatReversalRecoveryPaths.Combine(row.RecoveryPath, path);
        if (transactionId is not null)
            row.RecoveryTransactionId ??= transactionId;

        if (outstanding <= BigInteger.Zero)
        {
            row.Status = FiatReversalStatuses.Recovered;
            row.StatusReason = null;
            row.ResolvedAt = _clock.GetUtcNow();
            await MarkLotConsumedAsync(row, target, ct);
        }
        else
        {
            row.Status = FiatReversalStatuses.Recovering;
            row.StatusReason = Truncate(
                refusal ?? "Not everything this reversal took back could be recovered.", MaxReasonLength);
        }

        var saved = await SaveAsync(row, ct);
        if (saved.IsError)
        {
            // The ladder's own arithmetic is re-derivable from the chain and the provenance on
            // the next attempt, so an unwritten pass is retryable rather than lost.
            _logger.LogError(
                "Reversal {ReversalId} recovered {Amount} base units and the row could not be "
                + "updated: {Detail}",
                row.Id, ReversalMath.Format(recoveredThisPass), saved.Message);
            return Indeterminate(row, saved.Message);
        }

        return Definite(saved.Result!);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ReversalStepOutcome>> SettleShortfallAsync(
        string reversalId, CancellationToken ct = default)
    {
        var read = await _reversals.GetAsync(reversalId, ct);
        if (read.IsError)
            return AZOAResult<ReversalStepOutcome>.Failure(read.Message);
        if (read.Result is not { } row)
            return AZOAResult<ReversalStepOutcome>.Failure($"Reversal '{reversalId}' was not found.");

        if (FiatReversalStatuses.IsSettled(row.Status) || FiatReversalStatuses.NeedsPerson(row.Status))
            return Definite(row);

        var policy = _options.PolicyFor(row.FlowPurpose);

        // Still only a question, not a debit. Nothing to settle yet.
        if (string.Equals(row.Kind, FiatReversalKinds.Dispute, StringComparison.Ordinal)
            && !row.FundsWithdrawn
            && !policy.RecoverOnDisputeOpened)
        {
            return Definite(row);
        }

        var target = ReversalMath.ParseBaseUnits(row.TargetQuantityBaseUnits);
        var recovered = ReversalMath.ParseBaseUnits(row.RecoveredQuantityBaseUnits);
        var shortfall = target - recovered;

        var now = _clock.GetUtcNow();
        if (shortfall <= BigInteger.Zero)
        {
            row.Status = FiatReversalStatuses.Recovered;
            row.StatusReason = null;
            row.ResolvedAt = now;
        }
        else
        {
            // Rung 3. The platform is out of pocket for the difference and says so: the reserve
            // partition keeps counting these units as issued, because they ARE still issued --
            // somebody has them. Pretending otherwise would let the same declared budget be
            // handed out twice.
            row.AbsorbedQuantityBaseUnits = ReversalMath.Format(shortfall);
            row.RecoveryPath = FiatReversalRecoveryPaths.Combine(
                row.RecoveryPath, FiatReversalRecoveryPaths.Reserve);
            row.Status = recovered > BigInteger.Zero
                ? FiatReversalStatuses.PartiallyRecovered
                : FiatReversalStatuses.Absorbed;
            row.StatusReason = Truncate(BuildShortfallReason(row, shortfall), MaxReasonLength);
            row.ResolvedAt = now;

            if (policy.FlagAccountOnShortfall)
            {
                row.AccountFlagged = true;
                row.FlagReason = Truncate(
                    "Money for this account was taken back and could not be recovered in full.",
                    MaxReasonLength);
                row.FlagClearedAt = null;
                row.FlagClearedBy = null;
            }
        }

        await MarkLotConsumedAsync(row, target, ct);

        var saved = await SaveAsync(row, ct);
        if (saved.IsError)
            return Indeterminate(row, saved.Message);

        if (shortfall > BigInteger.Zero)
        {
            _logger.LogError(
                "Reversal {ReversalId} closed with a shortfall of {Shortfall} base units of {AssetId} "
                + "({Amount} {Currency} minor units taken back). The platform absorbed it; account "
                + "flagged={Flagged}.",
                row.Id, ReversalMath.Format(shortfall), row.AssetId, row.AmountMinorUnits,
                row.CurrencyCode, row.AccountFlagged);
        }

        return Definite(saved.Result!);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatReversal>> ParkForOperatorAsync(
        string reversalId, string reason, CancellationToken ct = default)
    {
        var read = await _reversals.GetAsync(reversalId, ct);
        if (read.IsError)
            return AZOAResult<FiatReversal>.Failure(read.Message);
        if (read.Result is not { } row)
            return AZOAResult<FiatReversal>.Failure($"Reversal '{reversalId}' was not found.");

        if (FiatReversalStatuses.NeedsPerson(row.Status))
            return AZOAResult<FiatReversal>.Success(row, "Already parked.");

        row.Status = FiatReversalStatuses.NeedsOperator;
        row.StatusReason = Truncate(reason, MaxReasonLength);
        row.AccountFlagged = true;
        row.FlagReason ??= Truncate(
            "A reversal against this account could not be unwound automatically.", MaxReasonLength);

        var saved = await SaveAsync(row, ct);
        if (saved.IsError)
        {
            _logger.LogError(
                "Reversal {ReversalId} could not be parked for an operator: {Detail}",
                row.Id, saved.Message);
            return saved;
        }

        _logger.LogError(
            "Reversal {ReversalId} needs a person: {Reason}", row.Id, row.StatusReason);
        return saved;
    }

    // ── Rung 1 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes units straight back out of the payer's own account, up to what is outstanding and
    /// never more than they actually hold.
    /// </summary>
    private async Task<RungResult> RecoverFromBalanceAsync(
        FiatReversal row, BigInteger outstanding, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(row.ChainType) || string.IsNullOrWhiteSpace(row.AssetId))
            return RungResult.Refused("The reversal does not name the units it has to take back.");

        if (!Guid.TryParseExact(row.SubjectUserId, "N", out var subjectUserId) || subjectUserId == Guid.Empty)
            return RungResult.Refused("The reversal does not name an account to take units back from.");

        if (!TryGetAsaModule(row.ChainType!, out var asa, out var moduleError))
            return RungResult.Refused(moduleError);

        var reserveAccount = await _reserve.GetAccountAsync(row.ChainType!, ct);
        if (reserveAccount.IsError || reserveAccount.Result is null)
            return RungResult.Retry($"The platform account could not be resolved: {reserveAccount.Message}");

        var holderAddress = await ResolveHolderAddressAsync(subjectUserId, row.ChainType!, ct);
        if (holderAddress.Address is null)
            return RungResult.Refused(holderAddress.Error);

        var holdingRead = await asa!.GetAssetHoldingAsync(row.AssetId!, holderAddress.Address, ct);
        if (holdingRead.IsError)
            return RungResult.Retry($"The account's balance could not be read: {holdingRead.Message}");

        var holding = ReversalMath.ParseBaseUnits(holdingRead.Result);
        var recoverable = holding < outstanding ? holding : outstanding;
        if (recoverable <= BigInteger.Zero)
            return RungResult.Refused("The account no longer holds the units this payment bought.");

        // The claim is keyed on the TARGET as well as the reversal, so a second partial refund
        // that raises the target unlocks a new move while a retry of THIS one never repeats it.
        var key = RecoveryIdempotencyKey(row, "balance", recoverable);
        var claim = await _idempotency.TryClaimAsync(key, RecoveryOperationType, ct);
        if (!claim.Won)
            return ReplayClaim(claim, recoverable);

        var clawback = await asa.ClawbackAssetAsync(
            row.AssetId!,
            holderAddress: holderAddress.Address,
            recipientAddress: reserveAccount.Result.Address,
            amount: RailAmount.OfChainAsset(recoverable, row.AssetId),
            signingContext: SigningContext.PlatformFor(row.ChainType!),
            clawbackAddress: reserveAccount.Result.Address,
            ct: ct);

        if (clawback.IsError)
        {
            // The provider proves every precondition against the node BEFORE signing, so a
            // refusal here means nothing was broadcast. Recording it as failed lets the next
            // attempt try again once the cause is fixed.
            await _idempotency.FailAsync(key, clawback.Message, ct);
            return RungResult.Retry($"The units could not be taken back: {clawback.Message}");
        }

        if (IsPendingConfirmation(clawback.Message))
        {
            // Submitted, outcome unknown. Retrying blind could take the units twice, so this is
            // where the reversal stops and asks for a person.
            await _idempotency.FailAsync(key, AmbiguousRecoveryReason, ct);
            return RungResult.Unconfirmed(AmbiguousRecoveryReason);
        }

        await _idempotency.CompleteAsync(key, clawback.Result ?? string.Empty, ct);

        // The units are back in the reserve, so the partition they were issued from gets its
        // availability back. Without this draw the on-ramp partition would shrink by every
        // chargeback the node successfully recovered from.
        var draw = await _reserve.DrawAsync(
            new ReserveDrawRequest(
                row.ChainType!,
                row.AssetId!,
                _onRamp.ReservePartition,
                ReserveDrawKind.Return,
                recoverable,
                key,
                // The node's own chargeback-recovery rail; the partition is node configuration.
                ReserveDrawAuthority.NodeOperator),
            ct);

        if (draw.IsError)
        {
            // The value moved and the counter did not. The draw carries its own replay anchor
            // (the same key), so the next pass re-applies it without moving anything again.
            _logger.LogError(
                "Reversal {ReversalId} took {Amount} base units back into the reserve but the "
                + "partition counter was not updated: {Detail}",
                row.Id, ReversalMath.Format(recoverable), draw.Message);
        }

        return RungResult.Recovered(
            recoverable, FiatReversalRecoveryPaths.Balance, clawback.Result);
    }

    // ── Rung 2 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unwinds what this payment's units were spent on — and only what can be ATTRIBUTED to this
    /// payment. Without that attribution nothing is touched: taking back an arbitrary hold a
    /// disputing sponsor happens to benefit from would rob whichever honest backer funded it.
    /// </summary>
    private async Task<RungResult> RecoverFromPurchasesAsync(
        FiatReversal row, ReversalFlowPolicy policy, BigInteger outstanding, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(row.LotId))
            return RungResult.Refused("This reversal is not linked to an issuance.");

        if (!_provenance.IsAvailable)
            return RungResult.Refused(UnavailableReversedPurchaseProvenance.UnavailableReason);

        if (!Guid.TryParseExact(row.SubjectUserId, "N", out var subjectUserId) || subjectUserId == Guid.Empty)
            return RungResult.Refused("The reversal does not name an account to unwind purchases for.");

        var claims = await _provenance.ListForLotAsync(row.LotId!, subjectUserId, ct);
        if (claims.IsError)
            return RungResult.Retry($"What this payment bought could not be read: {claims.Message}");

        var remaining = outstanding;
        var recovered = BigInteger.Zero;
        string? path = null;
        string? transactionId = null;
        string? refusal = null;

        foreach (var claim in claims.Result ?? Array.Empty<ReversedPurchaseClaim>())
        {
            if (remaining <= BigInteger.Zero)
                break;

            var attributable = claim.SourceQuantityBaseUnits < remaining
                ? claim.SourceQuantityBaseUnits
                : remaining;
            if (attributable <= BigInteger.Zero)
                continue;

            var rung = claim.Kind switch
            {
                ReversedPurchaseKind.OpenHold when policy.RecoverFromOpenHolds
                    => await RefundOpenHoldAsync(row, claim, attributable, ct),
                ReversedPurchaseKind.IssuedAsset when policy.RecoverFromPurchasedUnits
                    => await ClawBackPurchasedAssetAsync(row, claim, attributable, ct),
                _ => RungResult.Refused("This flow does not unwind that kind of commitment."),
            };

            if (rung.Ambiguous)
                return rung;
            if (!rung.Definite)
                return rung;

            if (rung.Amount > BigInteger.Zero)
            {
                recovered += rung.Amount;
                remaining -= rung.Amount;
                path = FiatReversalRecoveryPaths.Combine(path, rung.Path!);
                transactionId ??= rung.TransactionId;
            }
            else
            {
                refusal ??= rung.Detail;
            }
        }

        return recovered > BigInteger.Zero
            ? RungResult.Recovered(recovered, path ?? FiatReversalRecoveryPaths.Purchase, transactionId)
            : RungResult.Refused(refusal ?? "Nothing this payment bought could be unwound.");
    }

    /// <summary>
    /// Gives back a hold this payment funded. Nothing moves on any rail — the units never left
    /// the holding account — so the reserve's own <c>CancelHold</c> draw inside the hold service
    /// is the whole accounting, and this method deliberately performs no second draw.
    /// </summary>
    private async Task<RungResult> RefundOpenHoldAsync(
        FiatReversal row, ReversedPurchaseClaim claim, BigInteger attributable, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(claim.HoldId) || string.IsNullOrWhiteSpace(claim.ChainType))
            return RungResult.Refused("A commitment this payment funded names no hold to give back.");

        var key = RecoveryIdempotencyKey(row, $"hold:{claim.HoldId}", attributable);
        var idempotencyClaim = await _idempotency.TryClaimAsync(key, RecoveryOperationType, ct);
        if (!idempotencyClaim.Won)
            return ReplayClaim(idempotencyClaim, attributable, FiatReversalRecoveryPaths.Hold);

        var refunded = await _holds.RefundAsync(
            new HoldSettlementRequest(
                new HoldReference(claim.ChainType, claim.HoldId!),
                ActingTenantId: null,
                Reason: $"The payment that funded this hold was reversed ({row.Id})."),
            ct);

        if (refunded.IsError)
        {
            await _idempotency.FailAsync(key, refunded.Message, ct);
            return RungResult.Refused($"The hold could not be given back: {refunded.Message}");
        }

        await _idempotency.CompleteAsync(key, ReversalMath.Format(attributable), ct);
        return RungResult.Recovered(attributable, FiatReversalRecoveryPaths.Hold, null);
    }

    /// <summary>Takes back an asset this payment's units bought, using the reserve's clawback authority.</summary>
    private async Task<RungResult> ClawBackPurchasedAssetAsync(
        FiatReversal row, ReversedPurchaseClaim claim, BigInteger attributable, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(claim.AssetId)
            || string.IsNullOrWhiteSpace(claim.ChainType)
            || string.IsNullOrWhiteSpace(claim.HolderAddress)
            || claim.AssetQuantityBaseUnits <= BigInteger.Zero)
        {
            return RungResult.Refused(
                "What this payment bought is not described precisely enough to take back.");
        }

        if (!TryGetAsaModule(claim.ChainType, out var asa, out var moduleError))
            return RungResult.Refused(moduleError);

        var reserveAccount = await _reserve.GetAccountAsync(claim.ChainType, ct);
        if (reserveAccount.IsError || reserveAccount.Result is null)
            return RungResult.Retry($"The platform account could not be resolved: {reserveAccount.Message}");

        var key = RecoveryIdempotencyKey(row, $"asset:{claim.AssetId}", attributable);
        var idempotencyClaim = await _idempotency.TryClaimAsync(key, RecoveryOperationType, ct);
        if (!idempotencyClaim.Won)
            return ReplayClaim(idempotencyClaim, attributable, FiatReversalRecoveryPaths.Purchase);

        var clawback = await asa!.ClawbackAssetAsync(
            claim.AssetId!,
            holderAddress: claim.HolderAddress!,
            recipientAddress: reserveAccount.Result.Address,
            amount: RailAmount.OfChainAsset(claim.AssetQuantityBaseUnits, claim.AssetId),
            signingContext: SigningContext.PlatformFor(claim.ChainType),
            clawbackAddress: reserveAccount.Result.Address,
            ct: ct);

        if (clawback.IsError)
        {
            await _idempotency.FailAsync(key, clawback.Message, ct);
            return RungResult.Refused($"What this payment bought could not be taken back: {clawback.Message}");
        }

        if (IsPendingConfirmation(clawback.Message))
        {
            await _idempotency.FailAsync(key, AmbiguousRecoveryReason, ct);
            return RungResult.Unconfirmed(AmbiguousRecoveryReason);
        }

        await _idempotency.CompleteAsync(key, clawback.Result ?? string.Empty, ct);
        return RungResult.Recovered(attributable, FiatReversalRecoveryPaths.Purchase, clawback.Result);
    }

    // ── Shared machinery ──────────────────────────────────────────────────────

    /// <summary>
    /// Deterministic claim key for one recovery move. The target is part of it so a SECOND
    /// partial refund (which raises what is owed) unlocks a new move, while every retry of the
    /// same move replays instead of repeating.
    /// </summary>
    public static string RecoveryIdempotencyKey(FiatReversal row, string rung, BigInteger amount)
        => $"{RecoveryOperationType}:{row.Id}:{rung}:{ReversalMath.Format(amount)}";

    /// <summary>
    /// What a repeated claim means. A completed claim is a recovery that already happened; a
    /// failed one is a move whose outcome this node could not establish, which is the one case
    /// that must reach a person rather than a retry.
    /// </summary>
    private static RungResult ReplayClaim(
        IdempotencyClaim claim, BigInteger amount, string path = FiatReversalRecoveryPaths.Balance)
    {
        if (claim.Record.State == IdempotencyState.Completed)
            return RungResult.Recovered(amount, path, claim.Record.ResultPayload);

        if (claim.Record.State == IdempotencyState.Failed)
        {
            var error = claim.Record.Error ?? string.Empty;
            return error.Contains(AmbiguousRecoveryReason, StringComparison.Ordinal)
                ? RungResult.Unconfirmed(AmbiguousRecoveryReason)
                : RungResult.Retry($"A previous recovery attempt failed: {error}");
        }

        return RungResult.Retry("A recovery move for this reversal is still in progress.");
    }

    /// <summary>
    /// Marks the issuance this reversal unwound, so the source-aged cash-out rule (W8.1) can
    /// never hand out units the payment no longer paid for.
    /// </summary>
    /// <remarks>
    /// A FULL reversal moves the lot to <c>Reversed</c>, which the FIFO read already excludes. A
    /// PARTIAL one raises the lot's consumed watermark instead: from the cash-out rule's point of
    /// view a unit taken back by a chargeback and a unit already cashed out are the same unit —
    /// gone — so one watermark is the honest place for both, and two counters that could disagree
    /// with each other would not be.
    /// </remarks>
    private async Task MarkLotConsumedAsync(FiatReversal row, BigInteger target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(row.LotId) || target <= BigInteger.Zero)
            return;

        var read = await _lots.GetByIntentAsync(row.IntentId ?? string.Empty, ct);
        if (read.IsError || read.Result is not { } lot)
            return;

        var issued = ReversalMath.ParseBaseUnits(lot.QuantityBaseUnits);
        var alreadyConsumed = ReversalMath.ParseBaseUnits(lot.ConsumedBaseUnits);

        if (!row.IsPartial || target >= issued)
        {
            if (string.Equals(lot.Status, OnRampLotStatuses.Reversed, StringComparison.Ordinal))
                return;

            lot.Status = OnRampLotStatuses.Reversed;
            lot.StatusReason = Truncate(
                $"The payment that funded this issuance was reversed ({row.Kind.ToLowerInvariant()}).",
                MaxReasonLength);
        }
        else
        {
            var consumed = alreadyConsumed + target;
            if (consumed > issued)
                consumed = issued;
            if (consumed <= alreadyConsumed)
                return;

            lot.ConsumedBaseUnits = ReversalMath.Format(consumed);
        }

        lot.UpdatedAt = _clock.GetUtcNow();

        var expected = lot.Version;
        lot.Version = expected + 1;
        var saved = await _lots.UpdateIfVersionAsync(lot, expected, ct);
        if (saved.IsError)
        {
            lot.Version = expected;
            _logger.LogError(
                "Reversal {ReversalId} could not record itself against on-ramp lot {LotId}: {Detail}",
                row.Id, lot.Id, saved.Message);
        }
    }

    /// <summary>Compare-and-swap save that advances the row's own version anchor.</summary>
    private async Task<AZOAResult<FiatReversal>> SaveAsync(FiatReversal row, CancellationToken ct)
    {
        var expected = row.Version;
        row.Version = expected + 1;
        row.UpdatedAt = _clock.GetUtcNow();

        var saved = await _reversals.UpdateIfVersionAsync(row, expected, ct);
        if (saved.IsError)
        {
            // Put the in-memory row back where the caller found it so a caller that continues
            // does not reason over a version this node never persisted.
            row.Version = expected;
        }

        return saved;
    }

    private async Task<AZOAResult<ReversalStepOutcome>> ParkAmbiguousAsync(
        FiatReversal row, string? detail, CancellationToken ct)
    {
        var parked = await ParkForOperatorAsync(row.Id, detail ?? AmbiguousRecoveryReason, ct);
        return parked.IsError
            ? Indeterminate(row, parked.Message)
            : Definite(parked.Result!);
    }

    /// <summary>Resolves the ASA capability module for a chain. Fails closed when absent.</summary>
    private bool TryGetAsaModule(string chainType, out IAlgorandASAModule? module, out string error)
    {
        module = null;
        error = string.Empty;
        try
        {
            var provider = _providerFactory.GetProvider(chainType, _providerFactory.DefaultNetwork);
            if (!provider.TryGetModule<IAlgorandASAModule>(out module) || module is null)
            {
                error =
                    $"The '{chainType}' rail cannot take units back from an account, so this "
                    + "reversal cannot be unwound there.";
                return false;
            }

            return true;
        }
        catch (BlockchainProviderNotFoundException)
        {
            error = $"No provider is available for '{chainType}'.";
            return false;
        }
    }

    /// <summary>The payer's custodial account on one chain. Refuses rather than guessing.</summary>
    private async Task<(string? Address, string Error)> ResolveHolderAddressAsync(
        Guid subjectUserId, string chainType, CancellationToken ct)
    {
        var wallets = await _wallets.GetByUserAsync(subjectUserId, ct);
        if (wallets.IsError)
            return (null, $"The account's wallet could not be read: {wallets.Message}");

        var match = wallets.Result?.FirstOrDefault(wallet =>
            string.Equals(wallet.ChainType, chainType, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(wallet.Address));

        return match is null
            ? (null, $"The account has no {chainType} wallet to take units back from.")
            : (match.Address, string.Empty);
    }

    private string BuildShortfallReason(FiatReversal row, BigInteger shortfall)
    {
        var detail = string.IsNullOrWhiteSpace(row.StatusReason)
            ? "the units could not be recovered"
            : row.StatusReason!;

        var provenance = _provenance.IsAvailable
            ? string.Empty
            : " " + UnavailableReversedPurchaseProvenance.UnavailableReason;

        return
            $"{ReversalMath.Format(shortfall)} base units were not recovered ({detail}). "
            + $"The platform absorbed the difference.{provenance}";
    }

    private static bool IsPendingConfirmation(string? message)
        => message?.Contains(OperationStatus.PendingConfirmationMarker, StringComparison.Ordinal) == true;

    private static AZOAResult<ReversalStepOutcome> Definite(FiatReversal row)
        => AZOAResult<ReversalStepOutcome>.Success(new ReversalStepOutcome(row, true, null), "Success");

    private static AZOAResult<ReversalStepOutcome> Indeterminate(FiatReversal row, string? reason)
        => AZOAResult<ReversalStepOutcome>.Success(
            new ReversalStepOutcome(row, false, reason), "Indeterminate.");

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>What one rung concluded, before it is folded into the reversal row.</summary>
    private readonly record struct RungResult(
        BigInteger Amount, string? Path, string? TransactionId, bool Definite, bool Ambiguous, string? Detail)
    {
        /// <summary>The rung took units back.</summary>
        public static RungResult Recovered(BigInteger amount, string path, string? transactionId)
            => new(amount, path, transactionId, Definite: true, Ambiguous: false, Detail: null);

        /// <summary>The rung has a definite answer and it is "nothing here".</summary>
        public static RungResult Refused(string detail)
            => new(BigInteger.Zero, null, null, Definite: true, Ambiguous: false, Detail: detail);

        /// <summary>The rung could not establish an answer; trying again may.</summary>
        public static RungResult Retry(string detail)
            => new(BigInteger.Zero, null, null, Definite: false, Ambiguous: false, Detail: detail);

        /// <summary>A value move may have landed and could not be confirmed. Only a person may decide.</summary>
        /// <remarks>Named apart from the <c>Ambiguous</c> property it sets: C# forbids a
        /// member and a positional record property sharing one name.</remarks>
        public static RungResult Unconfirmed(string detail)
            => new(BigInteger.Zero, null, null, Definite: true, Ambiguous: true, Detail: detail);
    }
}
