using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Managers;

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
///
/// <para><b>Parked action runs.</b> Each tick ALSO drains the durable action runs
/// parked in <c>AwaitingReconciliation</c> via
/// <see cref="IParkedRunReconciler.ReconcileAllParkedRunsAsync"/> — before this the
/// backlog only moved when a human called
/// <c>POST /api/action/runs/reconcile-sweep</c>, so an issuance whose confirmation
/// read timed out sat pending indefinitely. Runs still parked after their re-probe
/// and older than <see cref="ReconciliationOptions.OperationHardStuckAfterSeconds"/>
/// raise the same "MANUAL INTERVENTION REQUIRED" alert the bridge G6 check uses.</para>
/// </summary>
public sealed class ReconciliationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReconciliationHostedService> _logger;
    private readonly ReconciliationOptions _options;
    private readonly TimeSpan _minimumInterval;

    public ReconciliationHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ReconciliationHostedService> logger,
        IOptions<ReconciliationOptions> options,
        TimeSpan? minimumInterval = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
        _minimumInterval = minimumInterval ?? TimeSpan.FromSeconds(10);
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
        var configuredInterval = TimeSpan.FromSeconds(Math.Max(0, _options.IntervalSeconds));
        var interval = configuredInterval < _minimumInterval ? _minimumInterval : configuredInterval;
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

            // Third leg of the tick: un-park durable action runs. Kept in the same
            // scope so one tick is one DI scope, and after the bridge/op passes so a
            // chain-truth read the earlier passes warmed is already cached.
            var parkedRuns = await ReconcileParkedActionRunsAsync(scope, stoppingToken);

            var needsAttention =
                combined.StuckFlagged > 0 || combined.Failed > 0 || combined.Errors > 0
                || parkedRuns.HardStuck.Count > 0 || parkedRuns.Errors > 0;

            if (needsAttention)
            {
                _logger.LogWarning(
                    "Reconciliation sweep complete with attention items — " +
                    "bridge[{Bridge}] ops[{Ops}] parkedRuns[{ParkedRuns}] combined[{Combined}]",
                    bridge, ops, parkedRuns, combined);
            }
            else
            {
                _logger.LogInformation(
                    "Reconciliation sweep complete — bridge[{Bridge}] ops[{Ops}] " +
                    "parkedRuns[{ParkedRuns}] combined[{Combined}]",
                    bridge, ops, parkedRuns, combined);
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

    /// <summary>
    /// Un-parks durable action runs stuck in <c>AwaitingReconciliation</c> and raises the
    /// hard-stuck operator alert for the ones the re-probe could not resolve (bridge G6
    /// pattern: log + count, never mutate, never re-broadcast). Returns the report so the
    /// tick log carries its counts.
    /// </summary>
    private async Task<ParkedRunSweepReport> ReconcileParkedActionRunsAsync(
        IServiceScope scope, CancellationToken ct)
    {
        // Resolution is guarded: a manager-construction fault must not sink the whole
        // tick (the bridge and operation passes have already done their work by now).
        IActionManager? actionManager;
        try
        {
            actionManager = scope.ServiceProvider.GetService<IActionManager>();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Reconciliation: parked-run sweep could not resolve IActionManager — runs parked in " +
                "AwaitingReconciliation were NOT swept this tick.");
            return ParkedRunSweepReport.Empty with { Errors = 1, Error = ex.Message };
        }

        if (actionManager is not IParkedRunReconciler reconciler)
        {
            // Loud, never silent: a swallowed cast here would restore exactly the gap
            // this pass closes — parked runs waiting for a human to name them.
            _logger.LogError(
                "Reconciliation: parked-run sweep is NOT running — the registered IActionManager " +
                "({Registered}) does not implement IParkedRunReconciler. Runs parked in " +
                "AwaitingReconciliation will only drain when an operator calls " +
                "POST /api/action/runs/reconcile-sweep.",
                actionManager?.GetType().FullName ?? "<not registered>");
            return ParkedRunSweepReport.Empty;
        }

        // Parked runs share the blockchain-operation thresholds: both are "a chain
        // effect whose truth we cannot derive". A dedicated ParkedRun* knob belongs in
        // ReconciliationOptions when that file is next open.
        var hardStuckAfter = TimeSpan.FromSeconds(Math.Max(0, _options.OperationHardStuckAfterSeconds));

        var report = await reconciler.ReconcileAllParkedRunsAsync(
            hardStuckAfter, _options.BatchSize, ct);

        if (report.Error is not null)
        {
            _logger.LogError(
                "Reconciliation: parked-run sweep could not list runs awaiting reconciliation " +
                "— nothing was swept this tick. {Error}",
                report.Error);
            return report;
        }

        if (report.HardStuck.Count > 0)
        {
            var sample = string.Join(", ", report.HardStuck
                .OrderByDescending(r => r.Age)
                .Take(5)
                .Select(r => $"{r.RunId} (parked {r.Age.TotalMinutes:F0}m, {r.IndeterminateNodes} unresolved node(s))"));

            _logger.LogError(
                "Reconciliation: MANUAL INTERVENTION REQUIRED — {Count} action run(s) are STILL " +
                "parked in AwaitingReconciliation after a re-probe and have been parked longer " +
                "than the {ThresholdSeconds}s hard-stuck threshold. Chain truth is not derivable " +
                "for them; the sweep will NEVER re-broadcast. Operator console: /admin/parked-runs. " +
                "Sample: [{Sample}]",
                report.HardStuck.Count, hardStuckAfter.TotalSeconds, sample);
        }

        return report;
    }
}
