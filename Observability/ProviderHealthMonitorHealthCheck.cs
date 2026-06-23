using Microsoft.Extensions.Diagnostics.HealthChecks;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Observability;

/// <summary>
/// Health check derived from IProviderHealthMonitor scores.
/// - Healthy   : no providers recorded, OR all recorded providers are healthy (IsHealthy=true).
/// - Degraded  : at least one provider is unhealthy but others remain healthy.
/// - Unhealthy : all recorded providers are unhealthy.
/// Never throws — all exceptions are caught and reported as Unhealthy.
/// </summary>
public sealed class ProviderHealthMonitorHealthCheck : IHealthCheck
{
    private readonly IProviderHealthMonitor _monitor;

    public ProviderHealthMonitorHealthCheck(IProviderHealthMonitor monitor)
    {
        _monitor = monitor;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyDictionary<string, ProviderHealthScore> scores = _monitor.GetScores();

            if (scores.Count == 0)
            {
                return Task.FromResult(
                    HealthCheckResult.Healthy("No provider data recorded yet."));
            }

            var total = scores.Count;
            var unhealthyProviders = scores.Values
                .Where(s => !s.IsHealthy)
                .Select(s => s.ProviderName)
                .ToList();

            if (unhealthyProviders.Count == 0)
            {
                return Task.FromResult(
                    HealthCheckResult.Healthy(
                        $"All {total} provider(s) healthy."));
            }

            if (unhealthyProviders.Count == total)
            {
                return Task.FromResult(
                    HealthCheckResult.Unhealthy(
                        $"All {total} provider(s) are unhealthy: {string.Join(", ", unhealthyProviders)}."));
            }

            // Partial degradation
            return Task.FromResult(
                new HealthCheckResult(
                    status: HealthStatus.Degraded,
                    description: $"{unhealthyProviders.Count}/{total} provider(s) unhealthy: {string.Join(", ", unhealthyProviders)}."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy(
                    description: "ProviderHealthMonitor check threw an unexpected exception.",
                    exception: ex));
        }
    }
}
