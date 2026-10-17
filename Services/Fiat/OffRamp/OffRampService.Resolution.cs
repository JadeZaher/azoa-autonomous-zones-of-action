// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>The two audited operator resolutions for a cash-out a person must decide.</summary>
public enum PayoutResolution
{
    /// <summary>Stripe shows the money left: record it as funded (or paid, when the bank payout is paid).</summary>
    RecordPaid,

    /// <summary>Stripe shows no money left (or it came back): give the person their units back.</summary>
    ReturnUnits,
}

/// <summary>Operator resolutions for parked cash-outs; see Services/Fiat/OffRamp/AGENTS.md §operator-resolution.</summary>
public sealed partial class OffRampService
{
    /// <summary>Stripe transfer group every cash-out transfer is created under.</summary>
    public static string TransferGroupFor(string payoutId) => "azoa-payout-" + payoutId;

    private const string AmbiguousTransactionMarker = " tx=";

    /// <summary>The ambiguity marker plus the submitted transaction id, so an operator resolution can re-read the chain.</summary>
    private static string AmbiguousWithTransaction(string? transactionId)
        => string.IsNullOrWhiteSpace(transactionId)
            ? AmbiguousMoveReason
            : AmbiguousMoveReason + AmbiguousTransactionMarker + transactionId.Trim();

    /// <inheritdoc />
    public async Task<AZOAResult<FiatPayout>> ResolveForOperatorAsync(
        string payoutId, PayoutResolution resolution, Guid actor, string reason, CancellationToken ct = default)
    {
        if (actor == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength)
            return AZOAResult<FiatPayout>.FailureWithCode(
                "An operator and a reason of 1–512 characters are required.", AzoaErrorCodes.InvalidRequest);

        var read = await _payouts.GetAsync(payoutId, ct);
        if (read.IsError)
            return AZOAResult<FiatPayout>.Failure(read.Message);
        if (read.Result is not { } row)
            return AZOAResult<FiatPayout>.FailureWithCode($"Cash-out '{payoutId}' was not found.", AzoaErrorCodes.NotFound);
        if (row.Environment != _fiat.ActiveEnvironment.Name || row.ChainNetwork != _providerFactory.DefaultNetwork.ToString())
            return Conflict("This payout belongs to a different payment or chain environment.");
        if (row.Status is not (FiatPayoutStatuses.NeedsOperator or FiatPayoutStatuses.Reversed))
            return Conflict($"A cash-out that is {row.Status} has no operator resolution.");

        // The provider's own transfer record is the evidence; a complete page is required.
        var listed = await _connect.ListTransfersByGroupAsync(
            _fiat.CredentialsFor(_fiat.ActiveEnvironment), TransferGroupFor(row.Id), ct);
        if (listed.IsError || listed.Result is not { HasMore: false } page)
            return Conflict("Stripe's transfer record for this cash-out could not be read completely. Nothing was changed.");
        var ours = page.Transfers.Where(transfer => transfer.IntentId == row.Id).ToList();
        if (ours.Count > 1)
            return Conflict("More than one transfer names this cash-out. Reconcile it in Stripe first.");
        var transfer = ours.SingleOrDefault();
        if (transfer is not null && !MatchesTransfer(row, transfer))
            return Conflict("The transfer Stripe holds does not match this cash-out.");

        var priorStatus = row.Status;
        var outcome = resolution switch
        {
            PayoutResolution.RecordPaid => await RecordPaidAsync(row, transfer, ct),
            PayoutResolution.ReturnUnits => await ReturnUnitsForOperatorAsync(row, transfer, ct),
            _ => AZOAResult<FiatPayout>.FailureWithCode("Unknown resolution.", AzoaErrorCodes.InvalidRequest),
        };
        if (outcome.IsError)
            return outcome;

        var resolved = outcome.Result!;
        resolved.RecoveryAuditJson = PayoutAudit.Append(resolved.RecoveryAuditJson, new PayoutAudit.Entry(
            actor, resolution == PayoutResolution.RecordPaid ? "record-paid" : "return-units", reason.Trim(),
            _clock.GetUtcNow(), priorStatus, transfer?.TransferId));
        resolved.UpdatedAt = _clock.GetUtcNow();
        var saved = await SaveAsync(resolved, ct);
        if (saved.IsError)
            return saved;

        _logger.LogWarning(
            "Operator {Actor} resolved cash-out {PayoutId} from {Prior} to {Status} ({Resolution}).",
            actor, row.Id, priorStatus, saved.Result!.Status, resolution);
        return saved;
    }

    /// <summary>Money left the platform: the transfer is recorded and the bank leg continues (or is recorded paid).</summary>
    private async Task<AZOAResult<FiatPayout>> RecordPaidAsync(FiatPayout row, StripeTransfer? transfer, CancellationToken ct)
    {
        if (row.Status != FiatPayoutStatuses.NeedsOperator)
            return Conflict("Only a parked cash-out can be recorded as paid.");
        if (transfer is null)
            return Conflict("Stripe holds no transfer for this cash-out, so it was not paid. Return the units instead.");
        if (transfer.Reversed || transfer.AmountReversedMinorUnits > 0)
            return Conflict("That transfer was reversed. Reconcile the reversal instead of recording it paid.");

        row.ProviderTransferId = transfer.TransferId;
        row.StatusReason = null;
        if (row.ProviderPayoutId is { } bankPayoutId && row.ConnectedAccountId is { } account)
        {
            var bank = await _connect.GetBankPayoutAsync(
                _fiat.CredentialsFor(_fiat.ActiveEnvironment), account, bankPayoutId, ct);
            if (bank.IsError || bank.Result is not { } payout || payout.PayoutId != bankPayoutId
                || payout.IntentId != row.Id || payout.AmountMinorUnits != row.AmountMinorUnits
                || !string.Equals(payout.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase)
                || payout.Livemode != (_fiat.ActiveEnvironment.Kind == RailEnvironmentKind.Live))
                return Conflict("The bank payout could not be verified against this cash-out.");
            if (payout.Status is "failed" or "canceled")
                return Conflict("The bank payout failed. Use the failed-bank recovery instead.");
            if (payout.Status == "paid")
            {
                row.Status = FiatPayoutStatuses.Paid;
                row.PaidAt ??= _clock.GetUtcNow();
                return AZOAResult<FiatPayout>.Success(row);
            }
        }

        // Funded but not yet paid out to the bank: the bank-payout lifecycle owns it from here.
        row.Status = FiatPayoutStatuses.BankPending;
        return AZOAResult<FiatPayout>.Success(row);
    }

    /// <summary>No money left (or it came back): the person's units are given back exactly once.</summary>
    private async Task<AZOAResult<FiatPayout>> ReturnUnitsForOperatorAsync(
        FiatPayout row, StripeTransfer? transfer, CancellationToken ct)
    {
        if (row.Status == FiatPayoutStatuses.Reversed)
            return await ReinstateReversedUnitsAsync(row, transfer, ct);

        if (transfer is not null)
            return Conflict("Money left the platform for this cash-out. Record it paid, or recover the failed bank payout.");

        var collected = await ResolveCollectionAsync(row, ct);
        if (collected is null)
            return Conflict("Whether the units were collected is still unknown on chain. Nothing was changed.");

        if (collected == false)
        {
            await ReleaseLotsAsync(SubjectOf(row), row.ReadConsumedLots(), ct);
            row.Status = FiatPayoutStatuses.Refused;
            row.StatusReason = "Closed by an operator: no units were collected and no money was sent.";
            return AZOAResult<FiatPayout>.Success(row);
        }

        var returned = await ReturnUnitsAsync(row, ct);
        if (returned is { } failure)
            return Conflict($"The units could not be given back yet ({failure}). Try again; the move is idempotent.");

        await ReleaseLotsAsync(SubjectOf(row), row.ReadConsumedLots(), ct);
        row.UnitsOwedBaseUnits = "0";
        row.Status = FiatPayoutStatuses.Returned;
        row.StatusReason = "Closed by an operator: no money was sent and the units were given back.";
        return AZOAResult<FiatPayout>.Success(row);
    }

    /// <summary>A reversed transfer: the units owed for the money Stripe confirms came back are reinstated.</summary>
    private async Task<AZOAResult<FiatPayout>> ReinstateReversedUnitsAsync(
        FiatPayout row, StripeTransfer? transfer, CancellationToken ct)
    {
        if (row.RestitutionKey is not null || row.RecoveryRequested)
            return Conflict("A failed-bank recovery owns this cash-out. Use the recovery route.");
        var owed = OffRampAging.ParseBaseUnits(row.UnitsOwedBaseUnits);
        if (owed <= BigInteger.Zero)
            return Conflict("Nothing is owed on this reversed cash-out.");
        if (transfer is null || transfer.AmountReversedMinorUnits < row.ReversedMinorUnits)
            return Conflict("Stripe does not confirm the reversal this cash-out recorded.");

        // One key per recorded reversal amount: a later, larger reversal reinstates only its own increment.
        var key = MoveIdempotencyKey(row.Id, $"reinstate-{row.ReversedMinorUnits}");
        var returned = await ReturnUnitsAsync(row, ct, owed, key);
        if (returned is { } failure)
            return Conflict($"The units could not be reinstated yet ({failure}). Try again; the move is idempotent.");

        row.RestoredQuantityBaseUnits = OffRampAging.Format(OffRampAging.ParseBaseUnits(row.RestoredQuantityBaseUnits) + owed);
        row.UnitsOwedBaseUnits = "0";
        row.StatusReason = "The units owed for the reversed money were reinstated by an operator.";
        return AZOAResult<FiatPayout>.Success(row);
    }

    /// <summary>True = units are in the reserve; false = provably never moved; null = still unknown.</summary>
    private async Task<bool?> ResolveCollectionAsync(FiatPayout row, CancellationToken ct)
    {
        var claim = await _idempotency.GetAsync(MoveIdempotencyKey(row.Id, "collect"), ct);
        if (claim is null)
            return false;
        if (claim.State == IdempotencyState.Completed)
            return true;
        if (claim.State == IdempotencyState.InProgress)
            return null;

        var error = claim.Error ?? string.Empty;
        if (!error.Contains(AmbiguousMoveReason, StringComparison.Ordinal))
            return false;

        // Ambiguous: only the chain can say. The submitted id was recorded with the marker.
        var marker = error.IndexOf(AmbiguousTransactionMarker, StringComparison.Ordinal);
        if (marker < 0 || !TryGetProvider(row.ChainType, out var provider, out _))
            return null;
        var transactionId = error[(marker + AmbiguousTransactionMarker.Length)..].Trim();
        var confirmation = await provider.GetTransactionConfirmationAsync(transactionId, ct);
        if (confirmation.IsError)
            return null;
        return confirmation.Result switch
        {
            ChainConfirmation.Confirmed => true,
            ChainConfirmation.FailedOnChain => false,
            _ => null,
        };
    }

    private bool MatchesTransfer(FiatPayout row, StripeTransfer transfer)
        => transfer.Livemode == (_fiat.ActiveEnvironment.Kind == RailEnvironmentKind.Live)
           && transfer.AmountMinorUnits == row.AmountMinorUnits
           && string.Equals(transfer.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase)
           && transfer.Destination == row.ConnectedAccountId;

    private static AZOAResult<FiatPayout> Conflict(string message)
        => AZOAResult<FiatPayout>.FailureWithCode(message, AzoaErrorCodes.Conflict);
}
