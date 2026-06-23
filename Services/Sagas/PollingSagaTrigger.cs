using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Sagas;

/// <summary>
/// The polling <see cref="ISagaTrigger"/>: fires <c>onTick</c> on a fixed
/// interval. This is the ONLY timing-aware component; swapping it for a
/// SurrealDB LIVE-query trigger later is a localized change (one class), with
/// no processor/handler/store-contract impact (spec: convergence into
/// surrealdb-migration).
///
/// <para>Resilience mirrors <c>ReconciliationHostedService</c>: a tick failure
/// is swallowed (guarded here AND by the processor), the interval is clamped to
/// a 1s floor so a misconfiguration cannot hot-loop, and the loop exits cleanly
/// on the stopping token — it must NEVER bubble out and crash the host.</para>
/// </summary>
public sealed class PollingSagaTrigger : ISagaTrigger
{
    private readonly ILogger<PollingSagaTrigger> _logger;
    private readonly SagaOptions _options;

    public PollingSagaTrigger(ILogger<PollingSagaTrigger> logger, IOptions<SagaOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task RunAsync(Func<CancellationToken, Task> onTick, CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds));
        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, _options.StartupDelaySeconds));

        _logger.LogInformation(
            "Saga polling trigger starting. StartupDelay={StartupDelay}s Interval={Interval}s " +
            "BatchSize={BatchSize} MaxAttempts={MaxAttempts} BaseBackoff={BaseBackoff}s " +
            "LeaseTimeout={Lease}s",
            startupDelay.TotalSeconds, interval.TotalSeconds, _options.BatchSize,
            _options.MaxAttempts, _options.BaseBackoffSeconds, _options.LeaseTimeoutSeconds);

        try
        {
            if (startupDelay > TimeSpan.Zero)
                await Task.Delay(startupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await onTick(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break; // host stopping — exit cleanly
                }
                catch (Exception ex)
                {
                    // Defence in depth: the processor already contains per-step
                    // and per-tick failures, but the trigger must never let one
                    // escape and stop the loop.
                    _logger.LogError(ex,
                        "Saga trigger tick failed — swallowed; will retry next interval.");
                }

                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown during the startup delay.
        }

        _logger.LogInformation("Saga polling trigger stopped.");
    }
}
