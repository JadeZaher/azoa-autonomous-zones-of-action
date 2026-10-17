// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat.Reversal;
using SurrealForge.Client;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>
/// Records reversals of the funding transfer to a connected account.
///
/// <para>It is the mirror of <c>FiatReversalEventHandler</c> (W4.3) and it records its fact in
/// the SAME ledger, because an operator looking at "money that moved and then un-moved" must see
/// one board rather than two. What it deliberately does NOT do is start the compensation saga:
/// that ladder takes units back from the payer, and on a reversed payout the payer is owed, not
/// owing. Running it here would claw units out of somebody whose bank refused the platform's own
/// payment.</para>
///
/// <para>Registered as <c>IFiatEventHandler</c> ONLY. It credits nothing, so it must never
/// satisfy the live-boot <c>IFiatSettlementHandler</c> probe.</para>
/// </summary>
public sealed class PayoutReversalEventHandler : IFiatEventHandler
{
    /// <summary>The one provider event this handler claims.</summary>
    public const string TransferReversedEventType = "transfer.reversed";

    /// <summary>Scope word for a payout reversal; part of the ledger record id.</summary>
    public const string PayoutScope = "payout";

    /// <summary>The flow the reversal policy is selected by, and the word on the operator board.</summary>
    public const string FlowPurpose = "off-ramp";

    private const int MaxReasonLength = 512;

    private readonly FiatOptions _fiat;
    private readonly IFiatPayoutLedger _payouts;
    private readonly IFiatReversalLedger _reversals;
    private readonly IStripeConnectGateway _connect;
    private readonly ILogger<PayoutReversalEventHandler> _logger;
    private readonly TimeProvider _clock;

    public PayoutReversalEventHandler(
        FiatOptions fiat,
        IFiatPayoutLedger payouts,
        IFiatReversalLedger reversals,
        IStripeConnectGateway connect,
        ILogger<PayoutReversalEventHandler> logger,
        TimeProvider? clock = null)
    {
        _fiat = fiat ?? throw new ArgumentNullException(nameof(fiat));
        _payouts = payouts ?? throw new ArgumentNullException(nameof(payouts));
        _reversals = reversals ?? throw new ArgumentNullException(nameof(reversals));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => "fiat-payout-reversal";

    /// <inheritdoc />
    public bool Handles(string eventType)
        => string.Equals(eventType, TransferReversedEventType, StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task<FiatEventOutcome> ApplyAsync(FiatEventContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The correlation is the payout id this node stamped into the transfer's own metadata at
        // send time. A transfer with none is a transfer this node did not make.
        if (context.IntentId is not { } payoutId)
        {
            _logger.LogWarning(
                "A {EventType} delivery carried no cash-out correlation; nothing was recorded.",
                context.EventType);
            return FiatEventOutcome.Ignored;
        }

        var read = await _payouts.GetAsync(payoutId.ToString("N"), ct);
        if (read.IsError)
            return FiatEventOutcome.Failed;
        if (read.Result is not { } row)
            return FiatEventOutcome.Ignored;
        if (row.Environment != context.Environment.Name) return FiatEventOutcome.Ignored;

        if (string.IsNullOrWhiteSpace(row.ProviderTransferId))
        {
            // A reversal for a cash-out this node never recorded sending. There is no transfer id
            // to re-read the money against, so no figure here would be trustworthy: park it.
            _logger.LogError(
                "A cash-out reversal arrived for {PayoutId}, which carries no transfer reference.",
                row.Id);
            await ParkAsync(
                row,
                "The provider reported this cash-out reversed, but the node holds no reference for "
                + "the payment it sent. Reconcile against the rail before crediting anything.",
                ct);
            return FiatEventOutcome.AcceptedPending;
        }

        // The body is a TRIGGER. How much came back is re-read from the provider — a body-supplied
        // reversal amount is the on-ramp's money-printer read backwards: it would decide how much
        // the node owes somebody.
        var fetched = await _connect.GetTransferAsync(
            _fiat.CredentialsFor(context.Environment), row.ProviderTransferId!, ct);
        if (fetched.IsError || fetched.Result is null)
        {
            _logger.LogWarning(
                "The transfer for cash-out {PayoutId} could not be re-read; the delivery is retryable.",
                row.Id);
            return FiatEventOutcome.Failed;
        }

        var transfer = fetched.Result;

        if (!string.Equals(transfer.TransferId, row.ProviderTransferId, StringComparison.Ordinal)
            || transfer.AmountMinorUnits != row.AmountMinorUnits
            || !string.Equals(transfer.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase)
            || transfer.AmountReversedMinorUnits > transfer.AmountMinorUnits)
            return FiatEventOutcome.Failed;

        if (transfer.Livemode != (context.Environment.Kind == RailEnvironmentKind.Live))
        {
            _logger.LogError(
                "The transfer for cash-out {PayoutId} reports livemode={Livemode} on a rail bound to "
                + "{Environment}; nothing was recorded.",
                row.Id, transfer.Livemode, context.Environment);
            return FiatEventOutcome.Failed;
        }

        if (transfer.AmountReversedMinorUnits <= 0)
        {
            // The delivery says reversed; the provider does not show it yet. A stale read is
            // transient, so this stays retryable rather than being written down as a fact.
            _logger.LogWarning(
                "A reversal delivery for cash-out {PayoutId} re-read as not reversed; the delivery is retryable.",
                row.Id);
            return FiatEventOutcome.Failed;
        }

        var quantity = OffRampAging.ParseBaseUnits(row.QuantityBaseUnits);
        var sourceMinorUnits = row.AmountMinorUnits;
        var reversedMinorUnits = Math.Max(row.ReversedMinorUnits, transfer.AmountReversedMinorUnits);

        // The units the node now owes back: the same proportional arithmetic the on-ramp reversal
        // pipeline uses, so a partial reversal is an exact proportion rather than an estimate.
        var owed = ReversalMath.ProportionalTarget(
            quantity, reversedMinorUnits, sourceMinorUnits);

        var recorded = await RecordReversalAsync(row, transfer, reversedMinorUnits, owed, context, ct);
        if (recorded.IsError || recorded.Result is null)
            return FiatEventOutcome.Failed;

        // Reconcile from the monotonic ledger after a retry or a stale provider read.
        reversedMinorUnits = recorded.Result.AmountMinorUnits;
        owed = OffRampAging.ParseBaseUnits(recorded.Result.ReinstatementOwedBaseUnits);
        if (row.ReversedMinorUnits >= reversedMinorUnits
            && string.Equals(SurrealLink.FromLink(row.ReversalId), recorded.Result.Id, StringComparison.Ordinal))
            return FiatEventOutcome.Applied;

        owed = System.Numerics.BigInteger.Max(System.Numerics.BigInteger.Zero, owed - OffRampAging.ParseBaseUnits(row.RestoredQuantityBaseUnits));
        row.Status = row.Status == FiatPayoutStatuses.Returned && owed == 0 ? FiatPayoutStatuses.Returned : FiatPayoutStatuses.Reversed;
        row.StatusReason = Truncate(
            $"The provider took {reversedMinorUnits} {row.CurrencyCode} minor units back "
            + "out of this cash-out. The node owes those units to the account again; re-issuing them "
            + "is an operator action.",
            MaxReasonLength);
        row.ReversedMinorUnits = reversedMinorUnits;
        row.ReversedAt ??= _clock.GetUtcNow();
        row.ReversalId = SurrealLink.ToLink(FiatReversal.SchemaNameConst, recorded.Result.Id);
        row.UnitsOwedBaseUnits = OffRampAging.Format(owed);
        row.UpdatedAt = _clock.GetUtcNow();

        var saved = await SaveAsync(row, ct);
        if (saved.IsError)
        {
            // The ledger fact landed and the payout row did not. A retry converges (the ledger
            // claim is create-if-absent), and saying "applied" here would leave a cash-out that
            // still reads as paid next to money that came back.
            _logger.LogError(
                "Cash-out {PayoutId} was recorded as reversed in the ledger but its own row could not "
                + "be updated: {Detail}",
                row.Id, saved.Message);
            return FiatEventOutcome.Failed;
        }

        _logger.LogError(
            "Cash-out {PayoutId} was reversed by the provider: {Amount} {Currency} minor units came "
            + "back and {Owed} base units are owed to the account.",
            row.Id, transfer.AmountReversedMinorUnits, row.CurrencyCode, row.UnitsOwedBaseUnits);

        return FiatEventOutcome.Applied;
    }

    /// <summary>
    /// Writes the reversal as a first-class fact in W4.3's ledger, at a status that asks for a
    /// person rather than one the compensation ladder would pick up.
    /// </summary>
    private async Task<AZOAResult<FiatReversal>> RecordReversalAsync(
        FiatPayout row,
        StripeTransfer transfer,
        long reversedMinorUnits,
        BigInteger owed,
        FiatEventContext context,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var candidate = new FiatReversal
        {
            // A distinct scope word from 'refund' and 'dispute', so a payout reversal can never
            // collide with a reversal of the payment that funded the same person's units.
            Id = FiatReversal.RecordIdFor(FiatRailIds.StripeProviderKey, PayoutScope, transfer.TransferId),
            Provider = FiatRailIds.StripeProviderKey,
            ProviderObjectId = transfer.TransferId,
            // The ledger's kind vocabulary is Refund or Dispute. Money returning to the platform
            // after a payout is a refund OF that payout; last_event_type and flow_purpose are what
            // say which direction it went, and the operator board reads both.
            Kind = FiatReversalKinds.Refund,
            LastEventType = TransferReversedEventType,
            ProviderEventId = context.Row.ProviderEventId,
            ChargeId = transfer.TransferId,
            SubjectUserId = row.SubjectUserId,
            TenantId = row.TenantId,
            FlowPurpose = FlowPurpose,
            AmountMinorUnits = reversedMinorUnits,
            CurrencyCode = row.CurrencyCode,
            SourceAmountMinorUnits = transfer.AmountMinorUnits > 0 ? transfer.AmountMinorUnits : row.AmountMinorUnits,
            IsPartial = reversedMinorUnits < row.AmountMinorUnits,
            // The money is already gone from the platform's balance; there is nothing to wait for.
            FundsWithdrawn = true,
            ChainType = row.ChainType,
            AssetId = row.AssetId,
            // Nothing is to be RECOVERED from the person: this reversal runs the other way. The
            // figure that matters is what the node owes them, which the ledger already has a
            // column for.
            TargetQuantityBaseUnits = "0",
            RecoveredQuantityBaseUnits = "0",
            AbsorbedQuantityBaseUnits = "0",
            ReinstatementOwedBaseUnits = OffRampAging.Format(owed),
            RecoveryPath = FiatReversalRecoveryPaths.None,
            // NeedsOperator, deliberately: both RecoverAsync and SettleShortfallAsync return
            // immediately for this status, so the clawback ladder can never run against somebody
            // whose bank refused the platform's own payment.
            Status = FiatReversalStatuses.NeedsOperator,
            StatusReason = Truncate(
                "A cash-out was sent and the provider took it back. The account is owed those units "
                + "again; re-issuing them is an operator action, and nothing is to be recovered from "
                + "the person.",
                MaxReasonLength),
            // NOT flagged. The platform's own payout bouncing is not evidence against the person,
            // and flagging would bar them from moving value for something they did not do.
            AccountFlagged = false,
            AttemptCount = 0,
            OpenedAt = now,
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var claimed = await _reversals.ClaimAsync(candidate, ct);
        if (claimed.IsError || claimed.Result is null)
            return AZOAResult<FiatReversal>.Failure(claimed.Message);

        var existing = claimed.Result.Row;
        if (claimed.Result.Created || existing.AmountMinorUnits >= reversedMinorUnits)
            return AZOAResult<FiatReversal>.Success(existing, "Recorded.");

        var expected = existing.Version;
        existing.AmountMinorUnits = reversedMinorUnits;
        existing.ReinstatementOwedBaseUnits = OffRampAging.Format(owed);
        existing.IsPartial = candidate.IsPartial;
        existing.ProviderEventId = context.Row.ProviderEventId;
        existing.Status = FiatReversalStatuses.NeedsOperator;
        existing.StatusReason = candidate.StatusReason;
        existing.UpdatedAt = now;
        existing.Version = expected + 1;
        return await _reversals.UpdateIfVersionAsync(existing, expected, ct);
    }

    private async Task ParkAsync(FiatPayout row, string reason, CancellationToken ct)
    {
        row.Status = FiatPayoutStatuses.NeedsOperator;
        row.StatusReason = Truncate(reason, MaxReasonLength);
        row.UpdatedAt = _clock.GetUtcNow();
        await SaveAsync(row, ct);
    }

    /// <summary>Compare-and-swap save that advances the row's own version anchor.</summary>
    private async Task<AZOAResult<FiatPayout>> SaveAsync(FiatPayout row, CancellationToken ct)
    {
        var expected = row.Version;
        row.Version = expected + 1;

        var saved = await _payouts.UpdateIfVersionAsync(row, expected, ct);
        if (saved.IsError)
            row.Version = expected;

        return saved;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
