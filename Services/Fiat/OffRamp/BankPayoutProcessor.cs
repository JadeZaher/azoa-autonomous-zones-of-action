using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>Reconciles connected-account bank payouts; see AGENTS.md.</summary>
public sealed class BankPayoutProcessor(
    FiatOptions options,
    IFiatPayoutLedger payouts,
    IStripeConnectGateway gateway,
    ILogger logger,
    TimeProvider clock)
{
    public async Task<AZOAResult<OffRampStepOutcome>> SendAsync(FiatPayout row, CancellationToken ct)
    {
        if (row.Environment != options.ActiveEnvironment.Name)
            return AZOAResult<OffRampStepOutcome>.Failure("This payout belongs to a different payment environment.");
        if (string.IsNullOrWhiteSpace(row.ConnectedAccountId) || string.IsNullOrWhiteSpace(row.ProviderTransferId))
            return await ParkAsync(row, "The bank payout has no durable funding reference.", ct);

        var credentials = options.CredentialsFor(options.ActiveEnvironment);
        if (!string.IsNullOrWhiteSpace(row.ProviderPayoutId))
        {
            var fetched = await gateway.GetBankPayoutAsync(credentials, row.ConnectedAccountId, row.ProviderPayoutId, ct);
            return fetched.IsError || fetched.Result is null
                ? Pending(row, fetched.Message)
                : await ApplyAsync(row, fetched.Result, ct);
        }

        if (row.BankPayoutStartedAt is { } started && clock.GetUtcNow() - started >= TimeSpan.FromHours(23))
            return await ParkAsync(row, "The bank payout outcome is unknown beyond its safe retry window. Reconcile with the provider.", ct);

        var funding = await gateway.GetTransferAsync(credentials, row.ProviderTransferId, ct);
        if (funding.IsError || funding.Result is not { } transfer || transfer.TransferId != row.ProviderTransferId
            || transfer.IntentId != row.Id || transfer.Destination != row.ConnectedAccountId
            || transfer.AmountMinorUnits != row.AmountMinorUnits || transfer.AmountReversedMinorUnits != 0
            || transfer.Livemode != (options.ActiveEnvironment.Kind == RailEnvironmentKind.Live)
            || !string.Equals(transfer.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase))
            return Pending(row, "The original funding transfer is not currently verified for this bank payout.");

        var account = await gateway.GetConnectedAccountAsync(credentials, row.ConnectedAccountId, ct);
        if (account.IsError || account.Result is null)
            return Pending(row, account.Message);
        if (account.Result.PayoutSchedule != "manual" || !account.Result.PayoutsEnabled
            || account.Result.AccountId != row.ConnectedAccountId)
            return await ParkAsync(row, "The bank payout account is no longer ready for manual payouts.", ct);

        if (row.BankPayoutStartedAt is null)
        {
            row.BankPayoutStartedAt = clock.GetUtcNow();
            var saved = await SaveAsync(row, ct);
            if (saved.IsError)
                return AZOAResult<OffRampStepOutcome>.Failure(saved.Message);
            row = saved.Result!;
        }

        var created = await gateway.CreateBankPayoutAsync(credentials, new StripeTransferRequest
        {
            PayoutId = row.Id, SubjectUserId = row.SubjectUserId,
            ConnectedAccountId = row.ConnectedAccountId!, AmountMinorUnits = row.AmountMinorUnits,
            Currency = row.CurrencyCode,
            IdempotencyKey = $"azoa-bank-payout-{row.Id}-{row.BankPayoutAttempt}",
        }, ct);

        if (created.IsError || created.Result is null)
        {
            if (created.Code == FiatPayoutErrors.AwaitingFundsCode)
            {
                row.BankPayoutAttempt++;
                row.BankPayoutStartedAt = null;
                row.StatusReason = "Waiting for the payment provider to make funds available.";
                var saved = await SaveAsync(row, ct);
                if (saved.IsError)
                    return AZOAResult<OffRampStepOutcome>.Failure(saved.Message);
            }
            else if (created.Code == FiatPayoutErrors.PayoutRefusedCode)
                return await ParkAsync(row, "The bank payout was refused. Funds remain with the connected account; reconcile before returning units.", ct);
            return Pending(row, created.Message);
        }
        return await ApplyAsync(row, created.Result, ct);
    }

    public async Task<FiatEventOutcome> ReconcileAsync(
        FiatPayout row, string accountId, string payoutId, CancellationToken ct)
    {
        if (row.Environment != options.ActiveEnvironment.Name) return FiatEventOutcome.Ignored;
        if (row.ConnectedAccountId != accountId || row.BankPayoutStartedAt is null
            || (row.ProviderPayoutId is { } known && known != payoutId))
            return FiatEventOutcome.Ignored;
        var fetched = await gateway.GetBankPayoutAsync(options.CredentialsFor(options.ActiveEnvironment), accountId, payoutId, ct);
        if (fetched.IsError || fetched.Result is null || fetched.Result.PayoutId != payoutId)
            return FiatEventOutcome.Failed;
        var applied = await ApplyAsync(row, fetched.Result, ct);
        return applied.IsError ? FiatEventOutcome.Failed : FiatEventOutcome.Applied;
    }

    private async Task<AZOAResult<OffRampStepOutcome>> ApplyAsync(
        FiatPayout row, StripeBankPayout payout, CancellationToken ct)
    {
        if (payout.IntentId != row.Id || payout.AmountMinorUnits != row.AmountMinorUnits
            || !string.Equals(payout.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase)
            || payout.Livemode != (options.ActiveEnvironment.Kind == RailEnvironmentKind.Live)
            || (row.ProviderPayoutId is { } known && known != payout.PayoutId)
            || string.IsNullOrWhiteSpace(payout.PayoutId))
            return AZOAResult<OffRampStepOutcome>.Failure("The bank payout facts do not match this cash-out.");

        row.ProviderPayoutId = payout.PayoutId;
        switch (payout.Status)
        {
            case "paid":
                if (row.RecoveryRequested || row.RestitutionKey is not null || row.Status is FiatPayoutStatuses.Reversed or FiatPayoutStatuses.Returned)
                    return Pending(row, "The funding transfer was reversed and needs reconciliation.");
                row.Status = FiatPayoutStatuses.Paid;
                row.PaidAt ??= clock.GetUtcNow();
                row.StatusReason = null;
                break;
            case "pending":
            case "in_transit":
                if (row.Status is FiatPayoutStatuses.Paid or FiatPayoutStatuses.Reversed or FiatPayoutStatuses.NeedsOperator)
                    return Pending(row, "Awaiting a final provider outcome.");
                row.Status = FiatPayoutStatuses.BankPending;
                break;
            case "failed":
            case "canceled":
                if (row.RecoveryRequested || row.RestitutionKey is not null || row.Status is FiatPayoutStatuses.Reversed or FiatPayoutStatuses.Returned)
                    return Pending(row, "The funding transfer was reversed and needs reconciliation.");
                return await ParkAsync(row, "The bank payout did not complete. Funds returned to the connected account; reconcile before returning units.", ct);
            default:
                return AZOAResult<OffRampStepOutcome>.Failure("The provider returned an unknown bank payout state.");
        }
        var saved = await SaveAsync(row, ct);
        return saved.IsError ? AZOAResult<OffRampStepOutcome>.Failure(saved.Message)
            : Pending(saved.Result!, "The bank payout is recorded.");
    }

    private async Task<AZOAResult<OffRampStepOutcome>> ParkAsync(FiatPayout row, string reason, CancellationToken ct)
    {
        row.Status = FiatPayoutStatuses.NeedsOperator;
        row.StatusReason = reason;
        logger.LogWarning("Bank payout {PayoutId} requires reconciliation: {Reason}", row.Id, reason);
        var saved = await SaveAsync(row, ct);
        return saved.IsError ? AZOAResult<OffRampStepOutcome>.Failure(saved.Message) : Pending(saved.Result!, reason);
    }

    private async Task<AZOAResult<FiatPayout>> SaveAsync(FiatPayout row, CancellationToken ct)
    {
        var expected = row.Version;
        row.Version++;
        row.UpdatedAt = clock.GetUtcNow();
        return await payouts.UpdateIfVersionAsync(row, expected, ct);
    }

    private static AZOAResult<OffRampStepOutcome> Pending(FiatPayout row, string reason)
        => AZOAResult<OffRampStepOutcome>.Success(new(row, true, reason));
}
