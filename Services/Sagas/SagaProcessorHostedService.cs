using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Sagas;

/// <summary>
/// Hosted singleton that drives the saga processor. Generalizes
/// <c>ReconciliationHostedService</c>: it owns NO timing logic itself (that is
/// the swappable <see cref="ISagaTrigger"/>) and creates a fresh DI scope per
/// tick to resolve the scoped <see cref="ISagaProcessor"/> (which in turn uses
/// the scoped store / handlers).
///
/// <para><b>Lifetime &amp; scoping.</b> A <see cref="BackgroundService"/> is a
/// singleton; <see cref="ISagaProcessor"/> and the per-aggregate stores it
/// consumes are scoped, so each tick opens its own scope and disposes it —
/// identical discipline to the reconciliation sweep.</para>
///
/// <para><b>Resilience.</b> The trigger guards every tick; this service adds a
/// last-ditch guard so the loop can never bubble an exception out of the hosted
/// service and tear down the app. Honors the stopping token.</para>
/// </summary>
public sealed class SagaProcessorHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISagaTrigger _trigger;
    private readonly ILogger<SagaProcessorHostedService> _logger;
    private readonly SagaOptions _options;

    public SagaProcessorHostedService(
        IServiceScopeFactory scopeFactory,
        ISagaTrigger trigger,
        ILogger<SagaProcessorHostedService> logger,
        IOptions<SagaOptions> options)
    {
        _scopeFactory = scopeFactory;
        _trigger = trigger;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "Saga processor is DISABLED (Sagas:Enabled=false). The scoped " +
                "ISagaProcessor can still be invoked directly.");
            return;
        }

        try
        {
            await _trigger.RunAsync(RunTickSafelyAsync, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Last-ditch guard: the loop must never bubble out of the hosted
            // service and tear down the app.
            _logger.LogError(ex,
                "Saga processor terminated unexpectedly — it will NOT restart until the " +
                "app is recycled. Investigate.");
        }

        _logger.LogInformation("Saga processor stopped.");
    }

    /// <summary>
    /// One trigger tick: fresh scope, resolve the scoped processor, drain due
    /// steps. All non-cancellation exceptions are contained here so a bad tick
    /// never escapes (the trigger also guards, defence in depth).
    /// </summary>
    private async Task RunTickSafelyAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<ISagaProcessor>();
            var processed = await processor.ProcessDueStepsAsync(ct);
            if (processed > 0)
                _logger.LogInformation("Saga tick processed {Processed} due step(s).", processed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // propagate so the trigger loop exits cleanly
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Saga tick failed — swallowed; will retry next interval.");
        }
    }
}
