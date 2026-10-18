// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using SurrealForge.Client;

namespace AZOA.WebAPI.Services.Fiat.Reversal;

/// <summary>
/// Records every reversal of a card payment as a first-class durable fact, and starts the
/// compensation saga once the money has actually gone.
///
/// <para>It implements <see cref="IFiatEventHandler"/> and deliberately NOT
/// <see cref="IFiatSettlementHandler"/>: it credits nothing, so its presence must never be
/// mistaken by <c>FiatRailStartup.Validate</c> for a wired money path.</para>
///
/// <para>See <c>Services/Fiat/Reversal/AGENTS.md</c> for the invariants: the reversed amount is
/// re-read from the provider and never parsed out of the delivery; the issuance being unwound is
/// found through the node's OWN durable lot rather than through provider metadata; and a dispute
/// that has merely been OPENED flags the account without taking anything back.</para>
/// </summary>
public sealed class FiatReversalEventHandler : IFiatEventHandler
{
    private readonly FiatOptions _fiat;
    private readonly ReversalOptions _options;
    private readonly IReversalFactSource _facts;
    private readonly IFiatReversalLedger _reversals;
    private readonly IOnRampLotLookup _lots;
    private readonly ISagaCoordinator _sagas;
    private readonly ILogger<FiatReversalEventHandler> _logger;
    private readonly TimeProvider _clock;

    public FiatReversalEventHandler(
        FiatOptions fiat,
        ReversalOptions options,
        IReversalFactSource facts,
        IFiatReversalLedger reversals,
        IOnRampLotLookup lots,
        ISagaCoordinator sagas,
        ILogger<FiatReversalEventHandler> logger,
        TimeProvider? clock = null)
    {
        _fiat = fiat ?? throw new ArgumentNullException(nameof(fiat));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _reversals = reversals ?? throw new ArgumentNullException(nameof(reversals));
        _lots = lots ?? throw new ArgumentNullException(nameof(lots));
        _sagas = sagas ?? throw new ArgumentNullException(nameof(sagas));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => "fiat-reversal";

    /// <inheritdoc />
    public bool Handles(string eventType) => FiatReversalEventTypes.IsHandled(eventType);

    /// <inheritdoc />
    public async Task<FiatEventOutcome> ApplyAsync(FiatEventContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var credentials = _fiat.CredentialsFor(context.Environment);
        var isLive = context.Environment.Kind == RailEnvironmentKind.Live;

        return string.Equals(context.EventType, FiatReversalEventTypes.ChargeRefunded, StringComparison.Ordinal)
            ? await ApplyRefundAsync(context, credentials, isLive, ct)
            : await ApplyDisputeAsync(context, credentials, isLive, ct);
    }

    // ── Refunds ───────────────────────────────────────────────────────────────

    private async Task<FiatEventOutcome> ApplyRefundAsync(
        FiatEventContext context, StripeCredentials credentials, bool isLive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.ChargeId))
        {
            _logger.LogWarning(
                "A {EventType} delivery named no charge, so there is nothing to unwind.",
                context.EventType);
            return FiatEventOutcome.Ignored;
        }

        // The body is a TRIGGER. How much came back is re-read from the provider: a
        // body-supplied figure would decide how many units this node takes OUT of an account.
        var read = await _facts.GetChargeAsync(credentials, context.ChargeId!, ct);
        if (read.IsError || read.Result is null)
        {
            _logger.LogWarning(
                "The charge behind a {EventType} delivery could not be re-read; the delivery is "
                + "retryable and nothing was recorded.",
                context.EventType);
            return FiatEventOutcome.Failed;
        }

        var charge = read.Result;
        if (charge.Livemode != isLive)
        {
            _logger.LogError(
                "A charge reported livemode={Livemode} on a rail bound to {Environment}; the reversal "
                + "was not recorded.",
                charge.Livemode, context.Environment);
            return FiatEventOutcome.Failed;
        }

        if (charge.AmountRefundedMinorUnits <= 0)
        {
            // The provider says nothing has actually been refunded. Recording a zero reversal
            // would put a fact on the ledger that the money never left.
            _logger.LogInformation(
                "A {EventType} delivery for charge {ChargeId} reports no refunded amount; nothing was "
                + "recorded.",
                context.EventType, charge.ChargeId);
            return FiatEventOutcome.Ignored;
        }

        var lot = await ResolveLotAsync(charge.ChargeId, charge.PaymentIntentId, ct);
        if (lot.IsError)
            return FiatEventOutcome.Failed;

        return await UpsertAsync(
            context,
            scope: FiatReversal.RefundScope,
            providerObjectId: charge.ChargeId,
            kind: FiatReversalKinds.Refund,
            chargeId: charge.ChargeId,
            paymentIntentId: charge.PaymentIntentId,
            amountMinorUnits: charge.AmountRefundedMinorUnits,
            chargeAmountMinorUnits: charge.AmountMinorUnits,
            currency: charge.Currency,
            // A refund is money the node has already sent back. There is no "pending" stage.
            fundsWithdrawn: true,
            reinstated: false,
            lot: lot.Result,
            ct: ct);
    }

    // ── Disputes ──────────────────────────────────────────────────────────────

    private async Task<FiatEventOutcome> ApplyDisputeAsync(
        FiatEventContext context, StripeCredentials credentials, bool isLive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.PaymentIntentId))
        {
            _logger.LogWarning(
                "A {EventType} delivery named no payment, so the dispute could not be tied to one of "
                + "this node's payments.",
                context.EventType);
            return FiatEventOutcome.Ignored;
        }

        // The dispute is looked up FROM the payment rather than taken from the body: a dispute
        // object carries its own metadata, so the body cannot prove which payment it belongs to.
        var read = await _facts.GetDisputeForPaymentAsync(credentials, context.PaymentIntentId!, ct);
        if (read.IsError || read.Result is null)
        {
            _logger.LogWarning(
                "The dispute behind a {EventType} delivery could not be re-read; the delivery is "
                + "retryable and nothing was recorded.",
                context.EventType);
            return FiatEventOutcome.Failed;
        }

        var dispute = read.Result;
        if (dispute.Livemode != isLive)
        {
            _logger.LogError(
                "A dispute reported livemode={Livemode} on a rail bound to {Environment}; the reversal "
                + "was not recorded.",
                dispute.Livemode, context.Environment);
            return FiatEventOutcome.Failed;
        }

        var lot = await ResolveLotAsync(dispute.ChargeId, context.PaymentIntentId, ct);
        if (lot.IsError)
            return FiatEventOutcome.Failed;

        var reinstated = string.Equals(
            context.EventType, FiatReversalEventTypes.DisputeFundsReinstated, StringComparison.Ordinal);

        return await UpsertAsync(
            context,
            scope: FiatReversal.DisputeScope,
            providerObjectId: dispute.DisputeId,
            kind: FiatReversalKinds.Dispute,
            chargeId: dispute.ChargeId,
            paymentIntentId: context.PaymentIntentId,
            amountMinorUnits: dispute.AmountMinorUnits,
            chargeAmountMinorUnits: dispute.AmountMinorUnits,
            currency: dispute.Currency,
            // The dispute's own balance impact, not the event name, decides whether the money is
            // gone: an event can be redelivered out of order, a balance cannot.
            fundsWithdrawn: dispute.FundsWithdrawn,
            reinstated: reinstated,
            lot: lot.Result,
            ct: ct);
    }

    // ── The one durable write ─────────────────────────────────────────────────

    private async Task<FiatEventOutcome> UpsertAsync(
        FiatEventContext context,
        string scope,
        string providerObjectId,
        string kind,
        string chargeId,
        string? paymentIntentId,
        long amountMinorUnits,
        long chargeAmountMinorUnits,
        string currency,
        bool fundsWithdrawn,
        bool reinstated,
        OnRampLot? lot,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var recordId = FiatReversal.RecordIdFor(FiatRailIds.StripeProviderKey, scope, providerObjectId);

        // The money the units were actually bought with is the honest denominator for a partial
        // reversal; the charge total stands in only when no issuance was correlated.
        var sourceMinorUnits = lot is { FiatMinorUnits: > 0 } ? lot.FiatMinorUnits : chargeAmountMinorUnits;

        var candidate = new FiatReversal
        {
            Id = recordId,
            Provider = FiatRailIds.StripeProviderKey,
            ProviderObjectId = providerObjectId,
            Kind = kind,
            LastEventType = context.EventType,
            ProviderEventId = context.Row.ProviderEventId,
            ChargeId = chargeId,
            PaymentIntentId = paymentIntentId,
            CheckoutSessionId = lot?.SourceCheckoutSessionId ?? context.CheckoutSessionId,
            IntentId = lot?.IntentId,
            LotId = lot is null ? null : SurrealLink.ToLink(OnRampLot.SchemaNameConst, lot.Id),
            SubjectUserId = lot?.SubjectUserId,
            TenantId = lot?.TenantId,
            FlowPurpose = lot is null ? null : OnRampSettlementHandler.IssuancePurpose,
            AmountMinorUnits = amountMinorUnits,
            CurrencyCode = string.IsNullOrWhiteSpace(currency)
                ? _fiat.Checkout.Currency.ToUpperInvariant()
                : currency.ToUpperInvariant(),
            SourceAmountMinorUnits = sourceMinorUnits,
            IsPartial = sourceMinorUnits > 0 && amountMinorUnits < sourceMinorUnits,
            FundsWithdrawn = fundsWithdrawn,
            ChainType = lot?.ChainType,
            AssetId = lot?.AssetId,
            Status = FiatReversalStatuses.Open,
            AttemptCount = 0,
            OpenedAt = now,
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        ApplyTarget(candidate, lot);

        var claimed = await _reversals.ClaimAsync(candidate, ct);
        if (claimed.IsError || claimed.Result is null)
        {
            _logger.LogError(
                "A {EventType} delivery for charge {ChargeId} could not be recorded: {Detail}",
                context.EventType, chargeId, claimed.Message);
            return FiatEventOutcome.Failed;
        }

        var row = claimed.Result.Row;

        // A row a person now owns is not moved by a redelivery: the reason on it is the record of
        // a decision somebody has to make, and overwriting it would erase that.
        if (FiatReversalStatuses.NeedsPerson(row.Status))
            return FiatEventOutcome.AcceptedPending;

        var policy = _options.PolicyFor(row.FlowPurpose);
        var wasWithdrawn = row.FundsWithdrawn;

        row.LastEventType = context.EventType;
        row.ProviderEventId = context.Row.ProviderEventId;
        row.PaymentIntentId ??= paymentIntentId;

        if (reinstated)
            return await ApplyReinstatementAsync(row, now, ct);

        // A later partial refund raises what is owed. Re-reading the provider's CUMULATIVE figure
        // is what makes two partial refunds accumulate instead of racing each other.
        if (amountMinorUnits > row.AmountMinorUnits)
        {
            row.AmountMinorUnits = amountMinorUnits;
            row.SourceAmountMinorUnits = sourceMinorUnits;
            row.IsPartial = sourceMinorUnits > 0 && amountMinorUnits < sourceMinorUnits;
            ApplyTarget(row, lot);

            // Money that was already settled is owed again. Re-open it rather than leaving a
            // closed row that understates what the node has had taken back -- but only when
            // there is something left to unwind: an UNMATCHED reversal has no issuance behind it
            // however large it grows, and re-opening it would put a row on the operator's list
            // that no step can ever close.
            var reopenable =
                ReversalMath.ParseBaseUnits(row.TargetQuantityBaseUnits)
                    > ReversalMath.ParseBaseUnits(row.RecoveredQuantityBaseUnits)
                && !string.Equals(row.Status, FiatReversalStatuses.Unmatched, StringComparison.Ordinal);

            if (reopenable && FiatReversalStatuses.IsSettled(row.Status))
            {
                row.Status = FiatReversalStatuses.Open;
                row.StatusReason = null;
                row.AbsorbedQuantityBaseUnits = "0";
                row.ResolvedAt = null;
            }
        }

        if (fundsWithdrawn)
            row.FundsWithdrawn = true;

        // A dispute that has merely been RAISED bars further value actions without taking
        // anything back. That is the proportionate response to a question that may still be
        // answered in the node's favour -- and it is a control, not a note: RiskHoldValueGate
        // reads exactly this column.
        if (string.Equals(kind, FiatReversalKinds.Dispute, StringComparison.Ordinal)
            && policy.FlagAccountOnDisputeOpened
            && !row.AccountFlagged
            && row.FlagClearedAt is null)
        {
            row.AccountFlagged = true;
            row.FlagReason = "A payment made from this account is being disputed.";
        }

        var startCompensation =
            row.Status == FiatReversalStatuses.Open
            && (row.FundsWithdrawn || policy.RecoverOnDisputeOpened)
            && ReversalMath.ParseBaseUnits(row.TargetQuantityBaseUnits) > BigInteger.Zero;

        if (startCompensation)
            row.Status = FiatReversalStatuses.Recovering;

        var saved = await SaveAsync(row, ct);
        if (saved.IsError)
        {
            _logger.LogError(
                "A {EventType} delivery for charge {ChargeId} could not update reversal {ReversalId}: "
                + "{Detail}",
                context.EventType, chargeId, row.Id, saved.Message);
            return FiatEventOutcome.Failed;
        }

        row = saved.Result!;

        if (!startCompensation)
        {
            LogRecorded(context, row, wasWithdrawn);
            return FiatEventOutcome.Applied;
        }

        try
        {
            await _sagas.StartAsync(
                FiatCompensationSaga.Name,
                FiatCompensationSaga.CorrelationKeyFor(row.Id),
                new FiatCompensationPayload(row.Id, row.ChargeId, row.LotId),
                ct);
        }
        catch (Exception exception)
        {
            // The row says "recovering" and nothing is driving it. Put it back so the provider's
            // own retry starts the saga; if even that fails the reversal is still visible on the
            // operator surface, which can start it by hand.
            _logger.LogError(
                exception,
                "Reversal {ReversalId} was recorded but its compensation saga could not be started.",
                row.Id);

            row.Status = FiatReversalStatuses.Open;
            var reverted = await SaveAsync(row, ct);
            if (reverted.IsError)
            {
                _logger.LogError(
                    "Reversal {ReversalId} is marked recovering with no saga driving it; start it from "
                    + "the operator surface.",
                    row.Id);
            }

            return FiatEventOutcome.Failed;
        }

        _logger.LogWarning(
            "Reversal {ReversalId} opened: {Amount} {Currency} minor units taken back from charge "
            + "{ChargeId}; {Target} base units of {AssetId} have to be unwound.",
            row.Id, row.AmountMinorUnits, row.CurrencyCode, row.ChargeId,
            row.TargetQuantityBaseUnits, row.AssetId);

        return FiatEventOutcome.Applied;
    }

    /// <summary>
    /// The provider gave the disputed money back. The reversal is undone — except for whatever
    /// this node had already clawed back, which it now owes the person again.
    /// </summary>
    private async Task<FiatEventOutcome> ApplyReinstatementAsync(
        FiatReversal row, DateTimeOffset now, CancellationToken ct)
    {
        var recovered = ReversalMath.ParseBaseUnits(row.RecoveredQuantityBaseUnits);

        row.FundsWithdrawn = false;
        row.ReinstatedAt = now;
        row.ResolvedAt = now;
        row.AccountFlagged = false;
        row.FlagClearedAt = now;
        row.FlagReason = null;

        if (recovered > BigInteger.Zero)
        {
            // Re-issuing units is the on-ramp's job, not this pipeline's. Saying so on the row and
            // stopping is honest; quietly keeping units the node no longer has a claim to is not.
            row.ReinstatementOwedBaseUnits = ReversalMath.Format(recovered);
            row.Status = FiatReversalStatuses.NeedsOperator;
            row.StatusReason =
                $"The disputed money was reinstated after {ReversalMath.Format(recovered)} base units "
                + "had already been taken back. The account is owed them again.";
        }
        else
        {
            row.Status = FiatReversalStatuses.Reinstated;
            row.StatusReason = null;
        }

        var saved = await SaveAsync(row, ct);
        if (saved.IsError)
        {
            _logger.LogError(
                "Reversal {ReversalId} could not record the reinstatement: {Detail}", row.Id, saved.Message);
            return FiatEventOutcome.Failed;
        }

        _logger.LogInformation(
            "Reversal {ReversalId} was reinstated; account flag cleared, {Owed} base units owed back.",
            row.Id, row.ReinstatementOwedBaseUnits);

        return FiatEventOutcome.Applied;
    }

    /// <summary>
    /// How many units this reversal has to take back. Nothing issued means nothing to unwind,
    /// which is a definite answer and not a failure.
    /// </summary>
    private static void ApplyTarget(FiatReversal row, OnRampLot? lot)
    {
        if (lot is null)
        {
            row.TargetQuantityBaseUnits = "0";
            row.Status = FiatReversalStatuses.Unmatched;
            row.StatusReason =
                "No issuance on this node could be matched to the payment, so there is nothing to "
                + "unwind. The money movement is recorded for the operator's books.";
            row.ResolvedAt = row.OpenedAt;
            return;
        }

        var issued = ReversalMath.ParseBaseUnits(lot.QuantityBaseUnits);
        if (issued <= BigInteger.Zero
            || !string.Equals(lot.Status, OnRampLotStatuses.Issued, StringComparison.Ordinal)
                && !string.Equals(lot.Status, OnRampLotStatuses.Reversed, StringComparison.Ordinal))
        {
            // The money cleared and the node never credited it (Refused, Stuck or still Pending).
            // The refund IS the closure of that, so nothing is owed back in units.
            row.TargetQuantityBaseUnits = "0";
            row.Status = FiatReversalStatuses.Unmatched;
            row.StatusReason =
                "The payment was never credited, so the reversal returns money the node already "
                + "owed. Nothing had to be taken back.";
            row.ResolvedAt = row.OpenedAt;
            return;
        }

        var target = ReversalMath.ProportionalTarget(
            issued, row.AmountMinorUnits, row.SourceAmountMinorUnits);

        row.TargetQuantityBaseUnits = ReversalMath.Format(target);
        if (row.Status == FiatReversalStatuses.Unmatched)
        {
            row.Status = FiatReversalStatuses.Open;
            row.StatusReason = null;
            row.ResolvedAt = null;
        }
    }

    /// <summary>The issuance a reversal unwinds, found through this node's own durable records.</summary>
    private async Task<AZOAResult<OnRampLot?>> ResolveLotAsync(
        string chargeId, string? paymentIntentId, CancellationToken ct)
    {
        var byCharge = await _lots.GetByChargeAsync(chargeId, ct);
        if (byCharge.IsError)
            return byCharge;
        if (byCharge.Result is not null)
            return byCharge;

        // The first delivery of a payment does not always carry a charge id, so a lot can exist
        // with only the payment recorded on it.
        if (string.IsNullOrWhiteSpace(paymentIntentId))
            return new AZOAResult<OnRampLot?> { Result = null, Message = "Not found." };

        return await _lots.GetByPaymentIntentAsync(paymentIntentId!, ct);
    }

    private void LogRecorded(FiatEventContext context, FiatReversal row, bool wasWithdrawn)
    {
        if (string.Equals(row.Status, FiatReversalStatuses.Unmatched, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "A {EventType} delivery for charge {ChargeId} was recorded as unmatched: {Reason}",
                context.EventType, row.ChargeId, row.StatusReason);
            return;
        }

        _logger.LogWarning(
            "Reversal {ReversalId} recorded from {EventType}: {Amount} {Currency} minor units, "
            + "funds withdrawn {Before} -> {After}, account flagged={Flagged}.",
            row.Id, context.EventType, row.AmountMinorUnits, row.CurrencyCode,
            wasWithdrawn, row.FundsWithdrawn, row.AccountFlagged);
    }

    /// <summary>Compare-and-swap save that advances the row's own version anchor.</summary>
    private async Task<AZOAResult<FiatReversal>> SaveAsync(FiatReversal row, CancellationToken ct)
    {
        var expected = row.Version;
        row.Version = expected + 1;
        row.UpdatedAt = _clock.GetUtcNow();

        var saved = await _reversals.UpdateIfVersionAsync(row, expected, ct);
        if (saved.IsError)
            row.Version = expected;

        return saved;
    }
}
