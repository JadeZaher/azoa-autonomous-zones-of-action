using System.Collections.Concurrent;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Services.Blockchain;

// Mission B: the provider-selection layer was deleted (single-provider reality).
// This monitor is retained ONLY to feed /health (ProviderHealthMonitorHealthCheck
// reads GetScores()). The record/mark surface is kept so future degradation
// signals can still be wired in; provider *selection* is gone.
public class ProviderHealthMonitor : IProviderHealthMonitor
{
    private readonly ConcurrentDictionary<string, ProviderHealthScore> _scores = new();

    public void RecordSuccess(string providerName, double latencyMs)
    {
        var score = _scores.AddOrUpdate(providerName,
            _ => new ProviderHealthScore
            {
                ProviderName = providerName,
                LastLatencyMs = latencyMs,
                IsHealthy = true,
                SuccessCount = 1,
                ConsecutiveFailures = 0,
                LastChecked = DateTime.UtcNow
            },
            (_, existing) =>
            {
                existing.LastLatencyMs = latencyMs;
                existing.SuccessCount++;
                existing.ConsecutiveFailures = 0;
                existing.IsHealthy = true;
                existing.LastChecked = DateTime.UtcNow;
                RecalculateScore(existing);
                return existing;
            });
    }

    public void RecordFailure(string providerName)
    {
        var score = _scores.AddOrUpdate(providerName,
            _ => new ProviderHealthScore
            {
                ProviderName = providerName,
                IsHealthy = false,
                FailureCount = 1,
                ConsecutiveFailures = 1,
                LastChecked = DateTime.UtcNow
            },
            (_, existing) =>
            {
                existing.FailureCount++;
                existing.ConsecutiveFailures++;
                existing.LastChecked = DateTime.UtcNow;

                if (existing.ConsecutiveFailures >= 5)
                    existing.IsHealthy = false;

                RecalculateScore(existing);
                return existing;
            });
    }

    public IReadOnlyDictionary<string, ProviderHealthScore> GetScores() => _scores;

    public void MarkUnhealthy(string providerName)
    {
        var score = _scores.GetOrAdd(providerName, _ => new ProviderHealthScore { ProviderName = providerName });
        score.IsHealthy = false;
        score.LastChecked = DateTime.UtcNow;
        RecalculateScore(score);
    }

    public void MarkHealthy(string providerName)
    {
        var score = _scores.GetOrAdd(providerName, _ => new ProviderHealthScore { ProviderName = providerName });
        score.IsHealthy = true;
        score.ConsecutiveFailures = 0;
        score.LastChecked = DateTime.UtcNow;
        RecalculateScore(score);
    }

    private void RecalculateScore(ProviderHealthScore score)
    {
        var total = score.SuccessCount + score.FailureCount;
        if (total == 0)
        {
            score.Score = 50;
            return;
        }

        var successRate = (double)score.SuccessCount / total;
        var latencyPenalty = Math.Min(score.LastLatencyMs / 100.0, 30);
        var failurePenalty = score.ConsecutiveFailures * 5;

        score.ErrorRate = 1.0 - successRate;
        score.Score = (int)Math.Clamp((successRate * 100) - latencyPenalty - failurePenalty, 0, 100);

        if (!score.IsHealthy)
            score.Score = 0;
    }
}
