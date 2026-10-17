using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat;

/// <summary>Replays verified durable events without inventing webhook bodies.</summary>
public sealed class FiatEventRecovery(
    ISurrealExecutor executor, FiatOptions options, IStripeWebhookInbox inbox,
    IEnumerable<IFiatEventHandler> handlers, OnRampRecovery recovery, ILogger<FiatEventRecovery> logger)
{
    /// <summary>Automatic replays per event before it is left to the operator board; see Services/Fiat/AGENTS.md §recovery-cap.</summary>
    public const long MaxAutomaticAttempts = 60;

    public async Task<AZOAResult<FiatEvent>> ReplayAsync(string id, CancellationToken ct)
    {
        if (!options.Enabled) return AZOAResult<FiatEvent>.Failure("The payment rail is disabled.");
        var row = await executor.QuerySingleAsync<FiatEvent>(SurrealQuery<FiatEvent>.Key(id), ct);
        return row is null ? AZOAResult<FiatEvent>.Failure("The event was not found.")
            : await FiatEventDispatcher.ApplyAsync(row, options.ActiveEnvironment, inbox, handlers, logger, ct);
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        if (!options.Enabled) return;
        var environment = options.ActiveEnvironment.Name;
        var rows = await executor.QueryAsync<FiatEvent>(SurrealQuery<FiatEvent>.From()
            .Where(row => row.Environment == environment && row.AttemptCount < MaxAutomaticAttempts
                && (row.Status == FiatEventStatuses.Received || row.Status == FiatEventStatuses.Failed))
            .OrderBy(row => row.AttemptCount).Limit(50), ct);
        foreach (var row in rows)
        {
            try { await FiatEventDispatcher.ApplyAsync(row, options.ActiveEnvironment, inbox, handlers, logger, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogError(exception, "Fiat event {EventId} could not be replayed.", row.Id); }
        }
        var pendingRefunds = await executor.QueryAsync<OnRampLot>(SurrealQuery<OnRampLot>.From()
            .Where(row => row.Status == OnRampLotStatuses.RefundPending && row.Environment == environment)
            .OrderBy(row => row.UpdatedAt).Limit(50), ct);
        foreach (var lot in pendingRefunds)
        {
            try
            {
                var attemptedAt = DateTimeOffset.UtcNow;
                var claim = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<OnRampLot>(lot.Id)
                    .Where(row => row.Version == lot.Version && row.Status == OnRampLotStatuses.RefundPending)
                    .Set(row => row.UpdatedAt, attemptedAt).Set(row => row.Version, lot.Version + 1).Build(), ct);
                claim.EnsureAllOk();
                var current = claim.GetValues<OnRampLot>(0).SingleOrDefault();
                if (current is not null) await recovery.ReconcileAsync(current, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogError(exception, "Refund for lot {LotId} could not be reconciled.", lot.Id); }
        }
    }
}

/// <summary>Retries unfinished payment events after provider delivery has stopped.</summary>
public sealed class FiatEventRecoveryWorker(IServiceScopeFactory scopes, ILogger<FiatEventRecoveryWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<FiatEventRecovery>().SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Fiat event recovery sweep failed."); }
        }
    }
}
