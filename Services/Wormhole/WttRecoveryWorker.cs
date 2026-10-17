using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Leases due WTT operations fairly and reconciles authorized wire, guardian proof and settlement.</summary>
public sealed class WttRecoveryWorker(IServiceScopeFactory scopes, ILogger<WttRecoveryWorker> logger) : BackgroundService
{
    internal async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWttOperationStore>();
        var recovery = scope.ServiceProvider.GetRequiredService<WttRecoveryService>();
        foreach (var row in await store.ReadDueAsync(50, ct))
        {
            ct.ThrowIfCancellationRequested();
            var id = SurrealRecordGuid.BareId(row.Id);
            var lease = row.State switch
            {
                "Active" => TimeSpan.FromSeconds(15),
                "AwaitingProof" => TimeSpan.FromSeconds(20),
                "NeedsOperator" => TimeSpan.FromMinutes(5),
                _ => TimeSpan.FromHours(1),
            };
            try
            {
                if (!await store.ClaimRecoveryAsync(id, lease, ct)) continue;
                await recovery.ReconcileAsync(id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogError(exception, "WTT recovery failed for {OperationId}", id); }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogError(exception, "WTT recovery scan failed"); }
        }
    }
}
