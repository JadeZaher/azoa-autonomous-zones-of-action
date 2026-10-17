using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>Advances exact accepted release mandates without authorizing participant acceptance or replacement generations.</summary>
public sealed class HoldReleaseMandateWorker(IServiceScopeFactory scopes, ILogger<HoldReleaseMandateWorker> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Release mandate scan failed."); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }

    /// <summary>Schedules a bounded due page before independently advancing each exact stored mandate.</summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IHoldReleaseMandateStore>();
        var service = scope.ServiceProvider.GetRequiredService<HoldReleaseMandateService>();
        var now = DateTimeOffset.UtcNow;
        foreach (var row in await store.ListDueAsync(now, 50, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!await store.ScheduleAsync(row, now.AddMinutes(1), ct)) continue;
                var result = await service.AdvanceAcceptedAsync(row.Id, ct);
                if (result.IsError) logger.LogWarning("Release mandate {MandateId} awaits exact authority, cost or participant recovery.", row.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogError(exception, "Release mandate {MandateId} failed to advance.", row.Id); }
        }
    }
}
