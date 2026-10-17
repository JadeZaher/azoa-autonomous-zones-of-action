using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>Observes, rebroadcasts, accounts for or abandons exchange sources without acquiring new custody or egress authority.</summary>
public sealed class ExchangeSourceReconciliationWorker(IServiceScopeFactory scopes, ILogger<ExchangeSourceReconciliationWorker> logger)
    : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Exchange source reconciliation scan failed."); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }

    /// <summary>Processes one due page; each source has an independent failure boundary.</summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IExchangeHoldSourceStore>();
        var service = scope.ServiceProvider.GetRequiredService<ExchangeHoldSourceService>();
        var now = DateTimeOffset.UtcNow;
        foreach (var row in await store.ListExchangeReconciliationAsync(now, 50, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!await store.ScheduleExchangeReconciliationAsync(row, now.AddSeconds(30), ct)) continue;
                var result = await service.ReconcileAsync(row.Id, ct);
                if (result.IsError) logger.LogWarning("Exchange source {SourceId} awaits authoritative settlement evidence.", row.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogError(exception, "Exchange source reconciliation failed for {SourceId}.", row.Id); }
        }
    }
}
