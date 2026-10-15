// SPDX-License-Identifier: UNLICENSED

using Microsoft.Extensions.Options;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>
/// Drives <see cref="IKycIdentityErasureService.RunRetentionSweepAsync"/> on a
/// bounded schedule.
///
/// <para>Without a driver, <c>retention_expires_at</c> is decorative: the column is
/// written on every binding and every screening row, nothing ever reads it, and the
/// retained personal fields live forever while the schema claims a retention policy.
/// A registered-but-uncalled erasure service is worse than an absent one, because it
/// looks like the policy is enforced.</para>
///
/// <para>Unlike <see cref="KycScreeningMonitorPollWorker"/> this worker makes NO
/// vendor calls, so it does not draw on the shared per-IP budget and needs no
/// operator opt-in to be safe. It is on by default for that reason: an operator
/// forgetting to enable erasure is a compliance failure, not a tuning choice.</para>
/// </summary>
public sealed class KycIdentityRetentionSweepWorker : BackgroundService
{
    private const int MinimumIntervalHours = 1;
    private const int MaximumIntervalHours = 24 * 7;

    /// <summary>Delay before the first sweep so boot is never contended with.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KycIdentitySettings _settings;
    private readonly ILogger<KycIdentityRetentionSweepWorker> _logger;

    public KycIdentityRetentionSweepWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<KycSettings> options,
        ILogger<KycIdentityRetentionSweepWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = options.Value.Identity;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Clamp(
            _settings.RetentionSweepIntervalHours,
            MinimumIntervalHours,
            MaximumIntervalHours));

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSweepAsync(stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>One sweep pass; exposed so a test drives it without a host lifetime.</summary>
    public async Task RunSweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var erasure = scope.ServiceProvider.GetRequiredService<IKycIdentityErasureService>();
            var swept = await erasure.RunRetentionSweepAsync(ct);
            if (swept.IsError)
            {
                _logger.LogWarning("KYC retention sweep failed: {Message}", swept.Message);
                return;
            }

            if (swept.Result > 0)
                _logger.LogInformation("KYC retention sweep erased {Count} record(s).", swept.Result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "KYC retention sweep threw.");
        }
    }
}
