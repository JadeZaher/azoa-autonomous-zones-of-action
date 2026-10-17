using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>Returns proven recovered funding as capped, reserved unit restitution.</summary>
public sealed class FailedBankPayoutRecovery(FiatOptions options, IFiatPayoutLedger ledger,
    IStripeConnectGateway gateway, TimeProvider clock,
    Func<FiatPayout, BigInteger, string, CancellationToken, Task<string?>> restoreUnits)
{
    public async Task<AZOAResult<FiatPayout>> RecoverAsync(FiatPayout row, Guid actor, string reason, CancellationToken ct)
    {
        if (row.Environment != options.ActiveEnvironment.Name || string.IsNullOrWhiteSpace(row.ProviderTransferId)
            || string.IsNullOrWhiteSpace(row.ProviderPayoutId) || string.IsNullOrWhiteSpace(row.ConnectedAccountId))
            return Fail("The original payout and funding references must be reconciled in their own environment.");
        if (row.Status == FiatPayoutStatuses.Returned) return AZOAResult<FiatPayout>.Success(row);
        if (actor == Guid.Empty && !row.RecoveryRequested) return Fail("An audited operator recovery request is required.");
        if (actor != Guid.Empty && (string.IsNullOrWhiteSpace(reason) || reason.Length > 512))
            return Fail("A recovery reason of 1–512 characters is required.");
        var credentials = options.CredentialsFor(options.ActiveEnvironment);
        var payout = await gateway.GetBankPayoutAsync(credentials, row.ConnectedAccountId!, row.ProviderPayoutId!, ct);
        if (payout.IsError || payout.Result is not { } bank || !MatchesBank(row, bank)
            || bank.Status is not ("failed" or "canceled"))
            return Fail("The bank payout is not authoritatively failed or canceled; units cannot be returned.");
        var fetched = await gateway.GetTransferAsync(credentials, row.ProviderTransferId!, ct);
        if (fetched.IsError || fetched.Result is not { } transfer || !MatchesTransfer(row, transfer))
            return Fail("The original funding transfer could not be verified.");
        if (!row.RecoveryRequested)
        {
            var audit = JsonSerializer.Deserialize<List<RecoveryAudit>>(row.RecoveryAuditJson) ?? [];
            audit.Add(new(actor, reason.Trim(), clock.GetUtcNow(), row.Environment, row.ProviderTransferId!, row.ProviderPayoutId!));
            row.RecoveryAuditJson = JsonSerializer.Serialize(audit);
            row.RecoveryRequested = true;
            var saved = await SaveAsync(row, ct);
            if (saved.IsError) return saved;
            row = saved.Result!;
        }
        if (transfer.AmountReversedMinorUnits < row.AmountMinorUnits)
        {
            if (row.RecoveryStartedAt is null)
            {
                row.RecoveryStartedAt = clock.GetUtcNow();
                row.RecoveryReversalAmount = row.AmountMinorUnits - transfer.AmountReversedMinorUnits;
                var saved = await SaveAsync(row, ct);
                if (saved.IsError) return saved;
                row = saved.Result!;
            }
            if (clock.GetUtcNow() - row.RecoveryStartedAt < TimeSpan.FromHours(23)
                && row.ProviderTransferReversalId is null)
            {
                var reversed = await gateway.ReverseTransferAsync(credentials, row.ProviderTransferId!,
                    row.RecoveryReversalAmount, row.Id, $"azoa-payout-reversal-{row.Id}-{row.RecoveryReversalAttempt}", ct);
                if (reversed.IsError && reversed.Code == FiatPayoutErrors.AwaitingFundsCode)
                {
                    row.RecoveryReversalAttempt++;
                    row.RecoveryStartedAt = null;
                    var refused = await SaveAsync(row, ct);
                    if (refused.IsError) return refused;
                    row = refused.Result!;
                }
                if (!reversed.IsError && reversed.Result is { } evidence)
                {
                    if (evidence.TransferId != row.ProviderTransferId || evidence.Amount != row.RecoveryReversalAmount
                        || string.IsNullOrWhiteSpace(evidence.Id)) return Fail("The funding reversal did not match this payout.");
                    row.ProviderTransferReversalId = evidence.Id;
                }
            }
            fetched = await gateway.GetTransferAsync(credentials, row.ProviderTransferId!, ct);
            if (fetched.IsError || fetched.Result is not { } returned || !MatchesTransfer(row, returned))
                return Fail("The returned funding could not be established.");
            transfer = returned;
        }
        var quantity = OffRampAging.ParseBaseUnits(row.QuantityBaseUnits);
        var restored = OffRampAging.ParseBaseUnits(row.RestoredQuantityBaseUnits);
        if (row.UnitsReturnedTransactionId is not null && restored == 0 && row.RestitutionKey is null)
            restored = quantity;
        if (quantity <= 0 || row.AmountMinorUnits <= 0 || restored < 0 || restored > quantity)
            return Fail("The restitution accounting is invalid.");
        var provenTarget = quantity * transfer.AmountReversedMinorUnits / row.AmountMinorUnits;
        row.ReversedMinorUnits = Math.Max(row.ReversedMinorUnits, transfer.AmountReversedMinorUnits);
        if (provenTarget <= restored)
        {
            row.StatusReason = transfer.AmountReversedMinorUnits == 0
                ? "Funding has not returned; no units can be restored. Reconcile the original transfer."
                : "All units backed by the currently returned funding have been restored.";
            return await SaveAsync(row, ct);
        }
        if (row.RestitutionKey is null)
        {
            var identity = $"{row.Environment}|{row.ChainNetwork}|{row.SubjectUserId}|{row.ProviderTransferId}|{row.ProviderPayoutId}|{row.BankPayoutAttempt}|{provenTarget}";
            row.RestitutionKey = "fiat-restitution:" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            row.RestitutionTargetBaseUnits = OffRampAging.Format(provenTarget);
            row.Status = FiatPayoutStatuses.Reversed;
            var saved = await SaveAsync(row, ct);
            if (saved.IsError) return saved;
            row = saved.Result!;
        }
        var reservedTarget = OffRampAging.ParseBaseUnits(row.RestitutionTargetBaseUnits);
        if (reservedTarget > provenTarget || reservedTarget <= restored)
            return Fail("The reserved restitution is not backed by current returned funding.");
        var finalBank = await gateway.GetBankPayoutAsync(credentials, row.ConnectedAccountId!, row.ProviderPayoutId!, ct);
        if (finalBank.IsError || finalBank.Result is not { } final || !MatchesBank(row, final)
            || final.Status is not ("failed" or "canceled"))
            return Fail("The bank payout changed during recovery; restitution is held for reconciliation.");
        var failure = await restoreUnits(row, reservedTarget - restored, row.RestitutionKey!, ct);
        if (failure is not null) return Fail(failure);
        row.RestoredQuantityBaseUnits = OffRampAging.Format(reservedTarget);
        row.UnitsOwedBaseUnits = OffRampAging.Format(BigInteger.Max(BigInteger.Zero, provenTarget - reservedTarget));
        row.RestitutionKey = null;
        row.Status = reservedTarget == quantity ? FiatPayoutStatuses.Returned : FiatPayoutStatuses.Reversed;
        row.RecoveryRequested = reservedTarget != quantity;
        row.StatusReason = reservedTarget == quantity ? "The failed payout's funding and units were returned."
            : "Units backed by the returned portion of funding were restored; the remainder awaits reconciliation.";
        return await SaveAsync(row, ct);
    }

    private bool MatchesBank(FiatPayout row, StripeBankPayout bank)
        => bank.PayoutId == row.ProviderPayoutId && bank.IntentId == row.Id
            && bank.AmountMinorUnits == row.AmountMinorUnits && bank.Livemode == (options.ActiveEnvironment.Kind == RailEnvironmentKind.Live)
            && string.Equals(bank.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase);

    private bool MatchesTransfer(FiatPayout row, StripeTransfer transfer)
        => transfer.TransferId == row.ProviderTransferId && transfer.IntentId == row.Id
            && transfer.Destination == row.ConnectedAccountId && transfer.AmountMinorUnits == row.AmountMinorUnits
            && transfer.AmountReversedMinorUnits >= 0 && transfer.AmountReversedMinorUnits <= row.AmountMinorUnits
            && transfer.Livemode == (options.ActiveEnvironment.Kind == RailEnvironmentKind.Live)
            && string.Equals(transfer.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase);

    private async Task<AZOAResult<FiatPayout>> SaveAsync(FiatPayout row, CancellationToken ct)
    {
        var expected = row.Version++;
        row.UpdatedAt = clock.GetUtcNow();
        return await ledger.UpdateIfVersionAsync(row, expected, ct);
    }

    private static AZOAResult<FiatPayout> Fail(string message) => AZOAResult<FiatPayout>.Failure(message);
    private sealed record RecoveryAudit(Guid ActorId, string Reason, DateTimeOffset At, string Environment, string TransferId, string PayoutId);
}
