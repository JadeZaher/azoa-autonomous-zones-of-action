namespace AZOA.WebAPI.Services.Auth;

/// <summary>Advances bounded canonical identity migration pages without blocking unrelated API startup.</summary>
public sealed class WalletAuthIdentityMigrationWorker(IServiceScopeFactory scopes, ILogger<WalletAuthIdentityMigrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<WalletAuthIdentityMigrationService>().SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Wallet identity migration page failed; wallet readiness remains closed"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
