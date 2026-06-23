using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Interfaces;

public interface IProviderHealthMonitor
{
    /// <summary>
    /// Record a successful operation for a provider.
    /// </summary>
    void RecordSuccess(string providerName, double latencyMs);

    /// <summary>
    /// Record a failed operation for a provider.
    /// </summary>
    void RecordFailure(string providerName);

    /// <summary>
    /// Get current health scores for all tracked providers.
    /// </summary>
    IReadOnlyDictionary<string, ProviderHealthScore> GetScores();

    /// <summary>
    /// Mark a provider as unhealthy (e.g., after a circuit breaker trips).
    /// </summary>
    void MarkUnhealthy(string providerName);

    /// <summary>
    /// Mark a provider as healthy again.
    /// </summary>
    void MarkHealthy(string providerName);
}
