using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Services.Dex;

/// <summary>Schedules fair bounded recovery for admitted swap transactions and unresolved accounting.</summary>
public sealed class SwapRecoveryWorker(IServiceScopeFactory scopes, ILogger<SwapRecoveryWorker> logger) : BackgroundService
{
    internal async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISwapOperationStore>();
        var recovery = scope.ServiceProvider.GetRequiredService<SwapRecoveryService>();
        foreach (var row in await store.ReadRecoveryDueAsync(50, ct))
        {
            ct.ThrowIfCancellationRequested();
            var id = SurrealRecordGuid.BareId(row.Id);
            try
            {
                if (await store.ClaimRecoveryAsync(id, row.Version, ct) is null) continue;
                await recovery.ReconcileAsync(id, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { logger.LogError(exception, "Swap recovery failed for {OperationId}", id); }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { logger.LogError(exception, "Swap recovery scan failed"); }
        }
    }
}
