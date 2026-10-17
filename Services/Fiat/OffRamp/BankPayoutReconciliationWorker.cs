using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>Reconciles bank payouts and pending provider funds after restarts or missed events.</summary>
public sealed class BankPayoutReconciliationWorker(
    IServiceScopeFactory scopes,
    FiatOptions fiat,
    OffRampOptions offRamp,
    ILogger<BankPayoutReconciliationWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!fiat.Enabled || !offRamp.Enabled)
                continue;
            try
            {
                using var scope = scopes.CreateScope();
                var executor = scope.ServiceProvider.GetRequiredService<ISurrealExecutor>();
                var environment = fiat.ActiveEnvironment.Name;
                var pending = await executor.QueryAsync<FiatPayout>(
                    SurrealQuery<FiatPayout>.From().Where(row => (row.Status == FiatPayoutStatuses.BankPending || row.RecoveryRequested) && row.Environment == environment),
                    stoppingToken);
                foreach (var row in pending)
                {
                    try
                    {
                        if (row.RecoveryRequested)
                        {
                            await scope.ServiceProvider.GetRequiredService<IOffRamp>().RecoverFailedBankAsync(row.Id, Guid.Empty, string.Empty, stoppingToken);
                            continue;
                        }
                        var result = await scope.ServiceProvider.GetRequiredService<IOffRamp>()
                            .SendMoneyAsync(row.Id, stoppingToken);
                        if (result.IsError)
                            logger.LogWarning("Bank payout {PayoutId} reconciliation will retry: {Reason}", row.Id, result.Message);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Bank payout {PayoutId} reconciliation failed; other payouts continue.", row.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Bank payout reconciliation pass failed; the next pass will retry.");
            }
        }
    }
}
