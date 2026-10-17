using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>Reconciles already-authorized hold wire without obtaining new participant signatures.</summary>
public sealed class HoldReconciliationWorker(IServiceScopeFactory scopes, ILogger<HoldReconciliationWorker> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Hold reconciliation scan failed."); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }

    /// <summary>Processes one due page; each record has an independent failure boundary.</summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IHoldCommandStore>();
        var executor = scope.ServiceProvider.GetRequiredService<HoldRailExecutor>();
        var now = DateTimeOffset.UtcNow;
        var due = await store.ListReconciliationAsync(now, 50, ct);
        foreach (var row in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!await store.ScheduleReconciliationAsync(row, now.AddSeconds(30), ct)) continue;
                var result = await executor.AdvanceAsync(row, false, ct);
                if (result.IsError) logger.LogWarning("Hold reconciliation {OperationId} awaits authoritative recovery.", row.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogError(exception, "Hold reconciliation failed for {OperationId}.", row.Id); }
        }
    }
}
