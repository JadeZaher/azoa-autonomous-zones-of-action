// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>
/// Critic rule 4: repairs stranded <c>Reserved</c> debits and promotes matured credits. See
/// <c>Services/Fiat/OffRamp/AGENTS.md</c> §tenant-funded (sweep).
/// </summary>
public sealed class TenantPayoutSweep(
    ITenantPayoutLedger ledger,
    ITenantPayoutFunding funding,
    IFiatPayoutLedger payouts,
    IOffRamp offRamp,
    IIdempotencyStore idempotency,
    TimeProvider clock,
    ILogger<TenantPayoutSweep> logger)
{
    /// <summary>A Reserved debit younger than this is left to the saga that owns it.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>A cash-out still Requested this long after its claim, with no collect attempt, never started.</summary>
    public static readonly TimeSpan AbandonRequestedAfter = TimeSpan.FromHours(1);

    /// <summary>Reason written when the sweep fences a collect that never started.</summary>
    public const string NeverStartedReason = "The cash-out never started (no collect attempt); the funding sweep closed it before any unit moved.";

    /// <summary>Debits read per page of the stale-reserved walk.</summary>
    public const int PageSize = 100;

    /// <summary>Pages one pass walks before yielding to the next tick (bounds a pass at 10 000 debits).</summary>
    public const int MaxPagesPerPass = 100;

    /// <summary>One bounded pass over every stale Reserved debit (M4: cursor-paged, not oldest-100); returns how many changed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await funding.PromoteMaturedAsync(ct);
        var now = clock.GetUtcNow();
        var changed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? cursor = null;
        for (var page = 0; page < MaxPagesPerPass; page++)
        {
            var batch = await ledger.ListStaleReservedAsync(now - StaleAfter, cursor, PageSize, ct);
            // The cursor is inclusive (ties on created_at); a page with nothing new means the walk is done.
            var fresh = batch.Where(debit => seen.Add(debit.Id)).ToList();
            if (fresh.Count == 0)
                break;
            foreach (var debit in fresh)
                changed += await SweepOneAsync(debit, now, ct);
            if (batch.Count < PageSize)
                break;
            cursor = batch[^1].CreatedAt;
        }

        return changed;
    }

    /// <summary>Repairs one stale debit from its payout's state; 1 when a transition committed.</summary>
    private async Task<int> SweepOneAsync(TenantPayoutDebit debit, DateTimeOffset now, CancellationToken ct)
    {
        var read = await payouts.GetAsync(debit.PayoutId, ct);
        if (read.IsError || read.Result is not { } row)
        {
            logger.LogError("Tenant payout debit {PayoutId} has no readable cash-out; it stays reserved.", debit.PayoutId);
            return 0;
        }

        switch (row.Status)
        {
            case FiatPayoutStatuses.Paid or FiatPayoutStatuses.Refused or FiatPayoutStatuses.Returned or FiatPayoutStatuses.Reversed:
                // A lost transition write (e.g. RefuseAsync's release) is repaired from the row.
                return await funding.SyncAsync(row, ct) == TenantFundingSync.Applied ? 1 : 0;
            case FiatPayoutStatuses.Requested when now - row.RequestedAt >= AbandonRequestedAfter:
                // Crash between the reservation and the saga start: fence the collect leg so a late
                // saga can never move units, then close the cash-out; SettleAsync releases the debit.
                var key = OffRampService.MoveIdempotencyKey(row.Id, "collect");
                var fence = await idempotency.TryClaimAsync(key, OffRampService.PayoutOperationType, ct);
                if (!fence.Won)
                    return 0;
                await idempotency.FailAsync(key, NeverStartedReason, ct);
                return (await offRamp.AbandonAsync(row.Id, NeverStartedReason, ct)).IsError ? 0 : 1;
            default:
                return 0;
        }
    }
}

/// <summary>Runs <see cref="TenantPayoutSweep"/> every minute while the off-ramp is enabled.</summary>
public sealed class TenantPayoutSweepWorker(
    IServiceScopeFactory scopes, FiatOptions fiat, OffRampOptions offRamp, ILogger<TenantPayoutSweepWorker> logger)
    : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!fiat.Enabled || !offRamp.Enabled)
                continue;
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<TenantPayoutSweep>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                // A worker survives a failed tick; the next tick re-reads durable state.
                logger.LogError(exception, "Tenant payout sweep tick failed.");
            }
        }
    }
}
