namespace AZOA.WebAPI.Core;

/// <summary>
/// Strategy for dynamic provider selection.
/// </summary>
public enum DynamicProviderMode
{
    /// <summary>
    /// Use explicit request or config default (current behavior).
    /// </summary>
    Off,

    /// <summary>
    /// Select provider with highest health score.
    /// </summary>
    HealthScore,

    /// <summary>
    /// Round-robin across all healthy providers.
    /// </summary>
    RoundRobin,

    /// <summary>
    /// Select provider with lowest latency.
    /// </summary>
    LowestLatency,

    /// <summary>
    /// Prefer config default; fallback to health score on failure.
    /// </summary>
    Adaptive
}
