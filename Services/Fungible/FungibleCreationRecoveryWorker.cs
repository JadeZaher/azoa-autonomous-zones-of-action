using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Services.Fungible;

/// <summary>Observes authorized creation wire and projects final evidence without new participant signatures.</summary>
public sealed class FungibleCreationRecoveryWorker(IServiceScopeFactory scopes, ILogger<FungibleCreationRecoveryWorker> logger) : BackgroundService
{
    internal async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFungibleCreationStore>();
        var service = scope.ServiceProvider.GetRequiredService<IFungibleCreationService>();
        foreach (var row in await store.ListRecoverableAsync(50, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await store.TouchRecoveryAsync(row, ct);
                var result = await service.RefreshAsync(row.Id, Guid.Parse(row.ActorUserId), row.TenantId is null ? null : Guid.Parse(row.TenantId), ct);
                if (result.IsError) logger.LogWarning("Fungible creation recovery refused for {OperationId}: {Reason}", row.Id, result.Message);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { logger.LogError(exception, "Fungible creation recovery failed for {OperationId}", row.Id); }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { logger.LogError(exception, "Fungible creation recovery scan failed"); }
        }
    }
}
