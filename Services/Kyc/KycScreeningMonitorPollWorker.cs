// SPDX-License-Identifier: UNLICENSED

using Microsoft.Extensions.Options;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>
/// Cold poll for AML ongoing monitoring. There is no push channel of ANY kind for
/// monitoring — the vendor's callback type list contains no screening event — so
/// this worker is the only way a monitored subject's status change is ever seen.
/// It exists precisely because there is no trigger signal to react to.
///
/// <para>It is deliberately slow and bounded. Every call it makes competes with
/// live KYC session traffic for one shared per-IP budget, and exceeding that budget
/// bans the node for an hour, so an aggressive poller would take the node's own KYC
/// offline to check on sanctions lists.</para>
/// </summary>
public sealed class KycScreeningMonitorPollWorker : BackgroundService
{
    private const int MinimumIntervalMinutes = 5;
    private const int MaximumIntervalMinutes = 24 * 60;

    /// <summary>Delay before the first cycle so boot traffic is never contended with.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IdentomatScreeningSettings _settings;
    private readonly bool _enabled;
    private readonly ILogger<KycScreeningMonitorPollWorker> _logger;

    public KycScreeningMonitorPollWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<KycSettings> options,
        ILogger<KycScreeningMonitorPollWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = options.Value.Identomat.Screening;
        _enabled = _settings.MonitorPollEnabled ?? !string.IsNullOrWhiteSpace(options.Value.Identomat.ConfigId);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation(
                "AML monitoring poll is off: no Identomat config id is set, or Kyc:Identomat:Screening:MonitorPollEnabled is false.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(
            _settings.MonitorPollIntervalMinutes,
            MinimumIntervalMinutes,
            MaximumIntervalMinutes));

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunCycleAsync(stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var screening = scope.ServiceProvider.GetRequiredService<IIdentomatScreeningService>();
            var report = await screening.PollMonitoringAsync(ct);
            if (report.IsError || report.Result is null)
            {
                _logger.LogWarning("AML monitoring poll cycle failed: {Message}", report.Message);
                return;
            }

            _logger.LogInformation(
                "AML monitoring poll examined {Examined} record(s) across {Pages} page(s); {Changed} changed, {FollowUps} follow-up fetch(es); budget-stopped={Stopped}.",
                report.Result.RecordsExamined,
                report.Result.PagesWalked,
                report.Result.RecordsChanged,
                report.Result.FollowUpsPerformed,
                report.Result.StoppedOnBudget);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "AML monitoring poll cycle threw.");
        }
    }
}
