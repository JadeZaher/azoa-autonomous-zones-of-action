using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Services.Users;

/// <summary>Converges previously admitted NFT transactions and projections without participant signing.</summary>
public sealed class NftLifecycleRecoveryWorker(IServiceScopeFactory scopes, ILogger<NftLifecycleRecoveryWorker> logger) : BackgroundService
{
    internal async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<INftLifecycleStore>();
        var reconciler = scope.ServiceProvider.GetRequiredService<INftLifecycleReconciler>();
        var rows = await store.ListPendingAsync(50, ct);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await reconciler.ReconcileAsync(row.Id, ct);
                if (result.IsError) logger.LogWarning("NFT reconciliation remains unresolved for {OperationId}: {Reason}", row.Id, result.Message);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { logger.LogError(exception, "NFT reconciliation failed for {OperationId}", row.Id); }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { logger.LogError(exception, "NFT recovery scan failed"); }
        }
    }
}
