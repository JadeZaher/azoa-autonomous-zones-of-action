// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.HostedSessions;

/// <summary>
/// Backoff poll of the authoritative result for attached Identomat KYC/KYB sessions whose callback was
/// lost or failed (F-H1). Veriff/generic sessions keep their attempt-based recovery worker.
/// See <c>Services/HostedSessions/AGENTS.md</c> §recovery.
/// </summary>
public sealed class HostedOutcomePollWorker(IServiceScopeFactory scopes, IOptions<KycSettings> options,
    TimeProvider clock, ILogger<HostedOutcomePollWorker> logger) : BackgroundService
{
    private readonly IdentomatOutcomePollSettings _settings = options.Value.Identomat.OutcomePoll;
    private readonly bool _enabled = options.Value.Identomat.OutcomePoll.Enabled
        ?? !string.IsNullOrWhiteSpace(options.Value.Identomat.ConfigId);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            logger.LogInformation("Hosted outcome poll is off: no Identomat config id is set, or Kyc:Identomat:OutcomePoll:Enabled is false.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(_settings.SweepIntervalSeconds, 5, 600)), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await SweepAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogError(exception, "Hosted outcome poll sweep failed."); }
        }
    }

    /// <summary>One sweep; returns how many sessions were re-fetched. See AGENTS.md §recovery (poll liveness).</summary>
    internal async Task<int> SweepAsync(IServiceProvider services, CancellationToken ct)
    {
        var budget = services.GetRequiredService<IIdentomatRateBudget>();
        if (budget.IsCircuitOpen)
            return 0;

        var recovery = services.GetRequiredService<IHostedSessionRecoveryStore>();
        var flow = services.GetRequiredService<IHostedSessionFlow>();
        var resolver = services.GetRequiredService<HostedIdentityProviderResolver>();
        var probe = services.GetService<IHostedSessionOutcomeHandler>() as IHostedSessionRelevanceProbe;
        var now = clock.GetUtcNow();
        var maxAge = TimeSpan.FromHours(Math.Clamp(_settings.MaxAgeHours, 1, 2160));
        var polled = 0;

        foreach (var session in await recovery.ListPollDueAsync(now, Math.Clamp(_settings.BatchSize, 1, 100), ct))
        {
            var id = Guid.ParseExact(session.Id, "N");
            var attempt = (session.PollAttemptCount ?? 0) + 1;
            var relevance = probe is null ? HostedOutcomeRelevance.Unknown : await probe.ProbeAsync(session, now, ct);
            var closeAs = relevance switch
            {
                // The bound attempt can no longer take a verdict: stop spending vendor budget on it.
                HostedOutcomeRelevance.Dead => HostedSessionRecoveryOutcomes.AttemptClosed,
                // A live attempt (incl. an extended IN_REVIEW) is polled until it expires, never cut at max age.
                HostedOutcomeRelevance.Unknown when session.CreatedDate <= now - maxAge => HostedSessionRecoveryOutcomes.PollExpired,
                _ => null,
            };
            if (closeAs is not null)
            {
                // A lost close (row moved) still advances next_poll_at so the row cannot pin the batch head.
                if (!await recovery.TryAbandonPolledAsync(session, closeAs, now, ct))
                    await recovery.TryClaimPollAsync(session, now + Backoff(attempt), ct);
                continue;
            }

            var provider = await resolver.ResolveAsync(session, ct);
            if (!await recovery.TryClaimPollAsync(session, now + Backoff(attempt), ct))
                continue;
            if (provider is null or IPreparedHostedSessionProvider)
                continue;

            var refreshed = await flow.RefreshAsync(provider, id, ct);
            polled++;
            // Only a node-wide refusal (local budget, open circuit after a 529) stops the sweep; a per-session
            // vendor 4xx is that row's problem and it already carries its backoff (RV-L2 L2-5).
            if (refreshed.IsError && (refreshed.Code == HostedSessionFlow.RequestNotSentCode || budget.IsCircuitOpen))
            {
                logger.LogWarning("Hosted outcome poll paused: the Identomat request budget is exhausted or the node is blocked.");
                break;
            }
        }

        return polled;
    }

    /// <summary>Exponential backoff: initial delay doubling per poll, capped at the configured ceiling.</summary>
    internal TimeSpan Backoff(long attempt)
    {
        var initial = TimeSpan.FromSeconds(Math.Clamp(_settings.InitialDelaySeconds, 10, 3600));
        var ceiling = TimeSpan.FromMinutes(Math.Clamp(_settings.MaxIntervalMinutes, 1, 1440));
        var exponent = (int)Math.Clamp(attempt - 1, 0, 20);
        var delay = TimeSpan.FromTicks(initial.Ticks * (1L << exponent));
        return delay < ceiling ? delay : ceiling;
    }
}
