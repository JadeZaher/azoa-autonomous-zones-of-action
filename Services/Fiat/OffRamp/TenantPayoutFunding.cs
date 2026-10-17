// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>Outcome of reserving a tenant-funded cash-out against its float.</summary>
public enum TenantFundingReservation
{
    /// <summary>The debit is (now, or already) Reserved for exactly this cash-out.</summary>
    Reserved,

    /// <summary>The float does not cover the amount; nothing was reserved.</summary>
    Insufficient,

    /// <summary>The float could not be read or written; nothing was reserved.</summary>
    Unavailable,
}

/// <summary>What one sync of a debit against its payout's evidence did.</summary>
public enum TenantFundingSync
{
    /// <summary>No debit, or nothing to change.</summary>
    Unchanged,

    /// <summary>A transition committed.</summary>
    Applied,

    /// <summary>The evidence could not be read, or a predicate lost; try again later.</summary>
    Retry,
}

/// <summary>A float as an operator sees it: counters, sums recomputed from entries, and whether they agree.</summary>
public sealed record TenantPayoutFloatView(
    string FloatId,
    Guid TenantId,
    string CurrencyCode,
    string Environment,
    long CreditedMinorUnits,
    long ReservedMinorUnits,
    long FinalizedMinorUnits,
    long AvailableMinorUnits,
    long DeficitMinorUnits,
    long PendingCreditMinorUnits,
    long RecomputedCreditedMinorUnits,
    long RecomputedReservedMinorUnits,
    long RecomputedFinalizedMinorUnits,
    bool Consistent);

/// <summary>D-8 tenant-funded payouts: reserve, finalize, release, reverse and credit the float exactly once.</summary>
public interface ITenantPayoutFunding
{
    /// <summary>Reserves <c>tenant_payout_debit:{payoutId}</c> against the funding tenant's float before anything moves.</summary>
    Task<TenantFundingReservation> ReserveAsync(FiatPayout row, CancellationToken ct = default);

    /// <summary>True when a Reserved debit for exactly this cash-out, owned by <paramref name="fundingTenantId"/>, exists.</summary>
    Task<bool> HoldsReservedDebitAsync(FiatPayout row, Guid fundingTenantId, CancellationToken ct = default);

    /// <summary>Applies the transition the payout's authoritative evidence supports (finalize, release, reversal).</summary>
    Task<TenantFundingSync> SyncAsync(FiatPayout row, CancellationToken ct = default);

    /// <summary>Records a settled top-up credit (Pending until matured); idempotent per checkout intent.</summary>
    Task<TenantPayoutCredit> RecordTopUpAsync(TenantPayoutCredit candidate, CancellationToken ct = default);

    /// <summary>Sets a credit's authoritative reversed total (refunds + dispute withdrawals, fees included).</summary>
    Task<TenantFundingSync> ApplyCreditReversalAsync(string creditId, long reversedMinorUnits, CancellationToken ct = default);

    /// <summary>Promotes matured Pending credits; returns how many were promoted.</summary>
    Task<int> PromoteMaturedAsync(CancellationToken ct = default);

    /// <summary>Every float a tenant holds, with the consistency check.</summary>
    Task<IReadOnlyList<TenantPayoutFloatView>> DescribeAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// D-8 domain service over <see cref="ITenantPayoutLedger"/>. See <c>Services/Fiat/OffRamp/AGENTS.md</c> §tenant-funded.
/// </summary>
public sealed class TenantPayoutFunding(
    ITenantPayoutLedger ledger,
    IIdempotencyStore idempotency,
    IStripeConnectGateway connect,
    FiatOptions fiat,
    TimeProvider clock,
    ILogger<TenantPayoutFunding> logger) : ITenantPayoutFunding
{
    private const int MaxAttempts = 4;

    /// <inheritdoc />
    public async Task<TenantFundingReservation> ReserveAsync(FiatPayout row, CancellationToken ct = default)
    {
        if (SurrealRecordGuid.ParseOptional(row.FundingTenantId) is not { } tenant || row.AmountMinorUnits <= 0)
            return TenantFundingReservation.Unavailable;

        var floatRow = await ledger.GetOrCreateFloatAsync(tenant, row.CurrencyCode, row.Environment, ct);
        await PromoteMaturedAsync(floatRow.Id, ct);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (await ledger.GetDebitAsync(row.Id, ct) is { } existing)
                return Matches(existing, row, tenant) && existing.State == TenantPayoutDebitStates.Reserved
                    ? TenantFundingReservation.Reserved
                    : TenantFundingReservation.Unavailable;

            var current = await ledger.GetFloatAsync(floatRow.Id, ct);
            if (current is null)
                return TenantFundingReservation.Unavailable;
            // Coverage: a negative float (deficit) covers nothing.
            if (current.Available() < row.AmountMinorUnits)
                return TenantFundingReservation.Insufficient;

            var now = clock.GetUtcNow();
            var debit = new TenantPayoutDebit
            {
                Id = row.Id,
                PayoutId = row.Id,
                FloatId = current.Id,
                TenantId = current.TenantId,
                CurrencyCode = current.CurrencyCode,
                Environment = current.Environment,
                AmountMinorUnits = row.AmountMinorUnits,
                ReversedMinorUnits = 0,
                State = TenantPayoutDebitStates.Reserved,
                Version = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var next = new TenantPayoutCounters(current.CreditedMinorUnits,
                current.ReservedMinorUnits + row.AmountMinorUnits, current.FinalizedMinorUnits);
            if (await ledger.CommitAsync(new TenantPayoutTransition(current, next, new TenantPayoutEntryChange.CreateDebit(debit)), ct))
                return TenantFundingReservation.Reserved;
        }

        return TenantFundingReservation.Unavailable;
    }

    /// <inheritdoc />
    public async Task<bool> HoldsReservedDebitAsync(FiatPayout row, Guid fundingTenantId, CancellationToken ct = default)
        => await ledger.GetDebitAsync(row.Id, ct) is { State: TenantPayoutDebitStates.Reserved } debit
           && Matches(debit, row, fundingTenantId);

    /// <inheritdoc />
    public async Task<TenantFundingSync> SyncAsync(FiatPayout row, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var debit = await ledger.GetDebitAsync(row.Id, ct);
            if (debit is null || debit.State == TenantPayoutDebitStates.Released)
                return TenantFundingSync.Unchanged;
            var floatRow = await ledger.GetFloatAsync(debit.FloatId, ct);
            if (floatRow is null)
                return TenantFundingSync.Retry;

            var evidence = await ReadTransferEvidenceAsync(row, ct);
            if (evidence is null)
                return TenantFundingSync.Retry;

            var plan = Plan(row, debit, evidence.Value);
            if (plan is null)
                return TenantFundingSync.Unchanged;

            var (toState, reversed) = plan.Value;
            var before = debit.Outstanding();
            var after = toState == TenantPayoutDebitStates.Released ? 0 : debit.AmountMinorUnits - reversed;
            var reserved = floatRow.ReservedMinorUnits - (debit.State == TenantPayoutDebitStates.Reserved ? before : 0)
                + (toState == TenantPayoutDebitStates.Reserved ? after : 0);
            var finalized = floatRow.FinalizedMinorUnits - (debit.State == TenantPayoutDebitStates.Finalized ? before : 0)
                + (toState == TenantPayoutDebitStates.Finalized ? after : 0);
            var transition = new TenantPayoutTransition(floatRow,
                new TenantPayoutCounters(floatRow.CreditedMinorUnits, reserved, finalized),
                new TenantPayoutEntryChange.UpdateDebit(debit.Id, debit.Version, debit.State, toState, reversed));
            if (await ledger.CommitAsync(transition, ct))
            {
                logger.LogInformation("Tenant payout debit {PayoutId}: {From} -> {To}, reversed {Reversed}.",
                    debit.Id, debit.State, toState, reversed);
                return TenantFundingSync.Applied;
            }
        }

        return TenantFundingSync.Retry;
    }

    /// <inheritdoc />
    public async Task<TenantPayoutCredit> RecordTopUpAsync(TenantPayoutCredit candidate, CancellationToken ct = default)
    {
        var recorded = await ledger.RecordCreditAsync(candidate, ct);
        await PromoteMaturedAsync(recorded.FloatId, ct);
        return recorded;
    }

    /// <inheritdoc />
    public async Task<TenantFundingSync> ApplyCreditReversalAsync(string creditId, long reversedMinorUnits, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var credit = await ledger.GetCreditAsync(creditId, ct);
            if (credit is null)
                return TenantFundingSync.Unchanged;
            var target = Math.Max(0, reversedMinorUnits);
            if (credit.ReversedMinorUnits == target)
                return TenantFundingSync.Unchanged;
            var floatRow = await ledger.GetFloatAsync(credit.FloatId, ct);
            if (floatRow is null)
                return TenantFundingSync.Retry;

            // Pending credits have not counted yet; the float row is still CAS-bumped so a racing
            // promotion serializes against this reversal (critic rule 3).
            var credited = credit.State == TenantPayoutCreditStates.Available
                ? floatRow.CreditedMinorUnits + credit.ReversedMinorUnits - target
                : floatRow.CreditedMinorUnits;
            var transition = new TenantPayoutTransition(floatRow,
                new TenantPayoutCounters(credited, floatRow.ReservedMinorUnits, floatRow.FinalizedMinorUnits),
                new TenantPayoutEntryChange.UpdateCredit(credit.Id, credit.Version, credit.State, credit.State, target));
            if (await ledger.CommitAsync(transition, ct))
                return TenantFundingSync.Applied;
        }

        return TenantFundingSync.Retry;
    }

    /// <inheritdoc />
    public Task<int> PromoteMaturedAsync(CancellationToken ct = default) => PromoteMaturedAsync(null, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantPayoutFloatView>> DescribeAsync(Guid tenantId, CancellationToken ct = default)
    {
        var views = new List<TenantPayoutFloatView>();
        foreach (var listed in await ledger.ListFloatsAsync(tenantId, ct))
        {
            if (await ledger.ReadSnapshotAsync(listed.Id, ct) is not { } snapshot)
                continue;
            var row = snapshot.Float;
            var credited = snapshot.Credits.Where(c => c.State == TenantPayoutCreditStates.Available).Sum(c => c.Contribution());
            var pending = snapshot.Credits.Where(c => c.State == TenantPayoutCreditStates.Pending).Sum(c => c.Contribution());
            var reserved = snapshot.Debits.Where(d => d.State == TenantPayoutDebitStates.Reserved).Sum(d => d.Outstanding());
            var finalized = snapshot.Debits.Where(d => d.State == TenantPayoutDebitStates.Finalized).Sum(d => d.Outstanding());
            var available = row.Available();
            views.Add(new TenantPayoutFloatView(row.Id, tenantId, row.CurrencyCode, row.Environment,
                row.CreditedMinorUnits, row.ReservedMinorUnits, row.FinalizedMinorUnits, available,
                available < 0 ? -available : 0, pending, credited, reserved, finalized,
                credited == row.CreditedMinorUnits && reserved == row.ReservedMinorUnits && finalized == row.FinalizedMinorUnits));
        }

        return views;
    }

    // ─── internals ───

    private async Task<int> PromoteMaturedAsync(string? floatId, CancellationToken ct)
    {
        var promoted = 0;
        foreach (var candidate in await ledger.ListMaturedPendingAsync(clock.GetUtcNow(), 50, ct))
        {
            if (floatId is not null && candidate.FloatId != floatId)
                continue;
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var credit = await ledger.GetCreditAsync(candidate.Id, ct);
                if (credit is not { State: TenantPayoutCreditStates.Pending } || credit.AvailableAt is not { } at || at > clock.GetUtcNow())
                    break;
                var floatRow = await ledger.GetFloatAsync(credit.FloatId, ct);
                if (floatRow is null)
                    break;
                // A later credit nets against a deficit first: credited simply rises by the contribution.
                var transition = new TenantPayoutTransition(floatRow,
                    new TenantPayoutCounters(floatRow.CreditedMinorUnits + credit.Contribution(),
                        floatRow.ReservedMinorUnits, floatRow.FinalizedMinorUnits),
                    new TenantPayoutEntryChange.UpdateCredit(credit.Id, credit.Version,
                        TenantPayoutCreditStates.Pending, TenantPayoutCreditStates.Available, credit.ReversedMinorUnits));
                if (await ledger.CommitAsync(transition, ct))
                {
                    promoted++;
                    break;
                }
            }
        }

        return promoted;
    }

    /// <summary>What Stripe holds for this cash-out's transfer group, or null when it cannot be read completely.</summary>
    private readonly record struct TransferEvidence(bool PayClaimExists, StripeTransfer? Transfer);

    private async Task<TransferEvidence?> ReadTransferEvidenceAsync(FiatPayout row, CancellationToken ct)
    {
        var payClaim = await idempotency.GetAsync(OffRampService.MoveIdempotencyKey(row.Id, "pay"), ct);
        if (payClaim is null && row.ProviderTransferId is null)
            return new TransferEvidence(false, null);

        var listed = await connect.ListTransfersByGroupAsync(
            fiat.CredentialsFor(fiat.ActiveEnvironment), OffRampService.TransferGroupFor(row.Id), ct);
        if (listed.IsError || listed.Result is not { HasMore: false } page)
            return null;
        var ours = page.Transfers.Where(transfer => transfer.IntentId == row.Id).ToList();
        return ours.Count switch
        {
            0 => new TransferEvidence(true, null),
            1 => new TransferEvidence(true, ours[0]),
            _ => null,
        };
    }

    /// <summary>The transition the evidence supports, or null when none applies yet.</summary>
    private static (string ToState, long Reversed)? Plan(FiatPayout row, TenantPayoutDebit debit, TransferEvidence evidence)
    {
        if (evidence.Transfer is { } transfer)
        {
            // Critic rule 2: one reversal source in every state, monotonic, capped at the debit.
            var reversed = Math.Min(debit.AmountMinorUnits, Math.Max(debit.ReversedMinorUnits, transfer.AmountReversedMinorUnits));
            var terminal = row.Status is FiatPayoutStatuses.Paid or FiatPayoutStatuses.Returned or FiatPayoutStatuses.Refused;
            var toState = debit.State == TenantPayoutDebitStates.Reserved && terminal
                ? TenantPayoutDebitStates.Finalized
                : debit.State;
            return toState == debit.State && reversed == debit.ReversedMinorUnits ? null : (toState, reversed);
        }

        // No transfer exists. Released only on a terminal no-money outcome (critic rule 1): either
        // no pay claim was ever taken, or a complete transfer-group page shows zero transfers.
        if (debit.State == TenantPayoutDebitStates.Reserved
            && row.Status is FiatPayoutStatuses.Refused or FiatPayoutStatuses.Returned)
            return (TenantPayoutDebitStates.Released, 0);

        return null;
    }

    private static bool Matches(TenantPayoutDebit debit, FiatPayout row, Guid tenant)
        => debit.PayoutId == row.Id && debit.AmountMinorUnits == row.AmountMinorUnits
           && SurrealRecordGuid.ParseOptional(debit.TenantId) == tenant
           && string.Equals(debit.CurrencyCode, row.CurrencyCode, StringComparison.OrdinalIgnoreCase)
           && debit.Environment == row.Environment;
}
