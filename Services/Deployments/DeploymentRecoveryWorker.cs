using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Services.Deployments;

/// <summary>Reconciles previously authorized exact native transactions.</summary>
public sealed class DeploymentRecoveryWorker(IServiceScopeFactory scopes, ILogger<DeploymentRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IDeploymentStore>();
                var service = scope.ServiceProvider.GetRequiredService<IDeploymentService>();
                foreach (var operation in await store.ListPendingAsync(100, stoppingToken))
                {
                    try { await service.ObserveAsync(operation.Id, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) { logger.LogError(exception, "Deployment recovery failed for {OperationId}", operation.Id); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Deployment recovery batch failed"); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
