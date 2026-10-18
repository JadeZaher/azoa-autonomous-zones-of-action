namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Hourly sweep of expired challenges, enrollments and sessions (M6).</summary>
public sealed class NodeAdminSweepWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<NodeAdminSweepWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var removed = await scope.ServiceProvider.GetRequiredService<SurrealNodeAdminStore>()
                    .SweepExpiredAsync(clock.GetUtcNow(), stoppingToken);
                if (removed > 0) logger.LogInformation("Node-admin sweep removed {Count} expired rows.", removed);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Node-admin sweep failed; retrying next interval.");
            }
        }
    }
}
