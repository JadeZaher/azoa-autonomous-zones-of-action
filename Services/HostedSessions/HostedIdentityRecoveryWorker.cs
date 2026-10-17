using AZOA.WebAPI.Interfaces.HostedSessions;

namespace AZOA.WebAPI.Services.HostedSessions;

/// <summary>Fairly resumes original identity attempts after lost provider responses or callbacks.</summary>
public sealed class HostedIdentityRecoveryWorker(IServiceScopeFactory scopes, ILogger<HostedIdentityRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await SweepAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Hosted identity recovery sweep failed"); }
        }
    }

    internal async Task SweepAsync(IServiceProvider services, CancellationToken ct)
    {
        var attempts = services.GetRequiredService<IHostedProviderAttemptStore>();
        var recovery = services.GetRequiredService<HostedIdentityRecovery>();
        foreach (var attempt in await attempts.ListDueAsync(DateTimeOffset.UtcNow, ct))
        {
            try { await recovery.RecoverAsync(attempt, null, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Hosted identity attempt {AttemptId} recovery failed", attempt.Id); }
        }
    }
}
