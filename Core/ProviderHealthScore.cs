namespace AZOA.WebAPI.Core;

/// <summary>
/// Health and performance score for a storage provider.
/// Higher Score = healthier / preferred provider.
/// </summary>
public class ProviderHealthScore
{
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Composite score (0-100). Calculated from latency, error rate, availability.
    /// </summary>
    public int Score { get; set; } = 50;

    /// <summary>
    /// Last measured latency in milliseconds.
    /// </summary>
    public double LastLatencyMs { get; set; }

    /// <summary>
    /// Error rate over the last window (0.0 - 1.0).
    /// </summary>
    public double ErrorRate { get; set; }

    /// <summary>
    /// Whether the provider is currently reachable.
    /// </summary>
    public bool IsHealthy { get; set; } = true;

    /// <summary>
    /// UTC timestamp of last health check.
    /// </summary>
    public DateTime LastChecked { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Consecutive failures since last success.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Total successful operations in the current window.
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Total failed operations in the current window.
    /// </summary>
    public int FailureCount { get; set; }
}
