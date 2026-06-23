using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Services.Reconciliation;

/// <summary>
/// Periodic background sweep that drives chain reconciliation
/// (api-safety-hardening plan tasks 14/15: "Background/triggered sweep for
/// stuck Redeeming/AwaitingVAA/AwaitingSignature records").
///
/// <para><b>Lifetime &amp; scoping.</b> A <see cref="BackgroundService"/> is a
/// singleton, but <see cref="IReconciliationService"/> and the per-aggregate
/// stores it consumes are scoped. Each tick therefore creates a fresh DI
/// scope via <see cref="IServiceScopeFactory"/> and resolves the scoped
/// service inside it, then disposes the scope.</para>
///
/// <para><b>Resilience.</b> A sweep failure (DB down, RPC flapping, etc.) is
/// logged and swallowed — it must NEVER crash the host. The loop also exits
/// cleanly on the application stopping token.</para>
///
/// <para><b>Manual trigger.</b> A targeted re-reconciliation is exposed via the
/// scoped <see cref="IReconciliationService"/> directly
/// (<c>ReconcileBridgeTransactionAsync</c>); wiring an admin endpoint to it is
/// out of scope for this service.</para>
/// </summary>
public sealed class ReconciliationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReconciliationHostedService> _logger;
    private readonly ReconciliationOptions _options;

    public ReconciliationHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ReconciliationHostedService> logger,
        IOptions<ReconciliationOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "Reconciliation background sweep is DISABLED (Reconciliation:Enabled=false). " +
                "The scoped IReconciliationService can still be invoked manually.");
            return;
        }

        // Clamp to a sane floor so a misconfigured tiny interval can't hot-loop.
        var interval = TimeSpan.FromSeconds(Math.Max(10, _options.IntervalSeconds));
        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, _options.StartupDelaySeconds));

        _logger.LogInformation(
            "Reconciliation background sweep starting. StartupDelay={StartupDelay}s " +
            "Interval={Interval}s BatchSize={BatchSize} " +
            "BridgeStaleAfter={BridgeStale}s BridgeHardStuck={BridgeHard}s " +
            "OpStaleAfter={OpStale}s OpHardStuck={OpHard}s",
            startupDelay.TotalSeconds, interval.TotalSeconds, _options.BatchSize,
            _options.BridgeStaleAfterSeconds, _options.BridgeHardStuckAfterSeconds,
            _options.OperationStaleAfterSeconds, _options.OperationHardStuckAfterSeconds);

        try
        {
            if (startupDelay > TimeSpan.Zero)
                await Task.Delay(startupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSweepSafelyAsync(stoppingToken);

                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break; // host stopping — exit cleanly
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown during the startup delay or a sweep await.
        }
        catch (Exception ex)
        {
            // Last-ditch guard: the loop itself must never bubble an exception
            // out of the hosted service and tear down the app.
            _logger.LogError(ex,
                "Reconciliation background sweep terminated unexpectedly — it will NOT restart " +
                "until the app is recycled. Investigate.");
        }

        _logger.LogInformation("Reconciliation background sweep stopped.");
    }

    /// <summary>
    /// One sweep tick: fresh scope, resolve the scoped service, run both
    /// reconciliations, log the combined report. All non-cancellation
    /// exceptions are contained here.
    /// </summary>
    private async Task RunSweepSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IReconciliationService>();

            var bridge = await svc.ReconcileBridgeAsync(stoppingToken);
            var ops = await svc.ReconcileOperationsAsync(stoppingToken);
            var combined = bridge.Combine(ops);

            if (combined.StuckFlagged > 0 || combined.Failed > 0 || combined.Errors > 0)
            {
                _logger.LogWarning(
                    "Reconciliation sweep complete with attention items — " +
                    "bridge[{Bridge}] ops[{Ops}] combined[{Combined}]",
                    bridge, ops, combined);
            }
            else
            {
                _logger.LogInformation(
                    "Reconciliation sweep complete — bridge[{Bridge}] ops[{Ops}] combined[{Combined}]",
                    bridge, ops, combined);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw; // propagate so ExecuteAsync exits the loop cleanly
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Reconciliation sweep failed — swallowed; will retry next interval.");
        }
    }
}
