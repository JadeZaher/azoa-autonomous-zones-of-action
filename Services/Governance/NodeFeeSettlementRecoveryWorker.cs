using AZOA.WebAPI.Interfaces.Managers;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>
/// Hosted node-fee settlement recovery: each tick reconciles accepted atomic groups against the
/// chain, then defers the remaining due settlements; see Services/Governance/AGENTS.md §node-fee-recovery.
/// </summary>
public sealed class NodeFeeSettlementRecoveryWorker(
    IServiceScopeFactory scopes, TimeProvider clock, ILogger<NodeFeeSettlementRecoveryWorker> logger)
    : BackgroundService
{
    /// <summary>Tick interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private const int BatchSize = 50;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                // A worker survives a failed tick; the next tick re-reads durable state.
                logger.LogError(exception, "Node fee settlement recovery tick failed.");
            }
        }
    }

    /// <summary>One bounded sweep: chain reconciliation of accepted groups first, then deferral of the rest.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var request = new NodeFeeSettlementRecoveryRequest(clock.GetUtcNow(), BatchSize, LeaseDuration, RetryDelay);

        // Reconcile first: a deferral would otherwise push an accepted group's next attempt past this tick.
        var reconciler = scope.ServiceProvider.GetService<INodeFeeSettlementAtomicGroupReconciler>();
        if (reconciler is not null)
        {
            var reconciled = await reconciler.ReconcileDueAsync(request, ct);
            if (reconciled.IsError)
                logger.LogWarning("Node fee atomic-group reconciliation did not run: {Detail}", reconciled.Message);
        }

        var settlements = scope.ServiceProvider.GetService<INodeFeeSettlementManager>();
        if (settlements is not null)
        {
            var recovered = await settlements.RecoverDueAsync(request, ct);
            if (recovered.IsError)
                logger.LogWarning("Node fee settlement recovery did not run: {Detail}", recovered.Message);
        }
    }
}
