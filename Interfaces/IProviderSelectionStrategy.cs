using OASIS.WebAPI.Core;

namespace OASIS.WebAPI.Interfaces;

/// <summary>
/// Pluggable strategy for selecting the best storage provider.
/// Implement this to add custom provider selection logic.
/// </summary>
public interface IProviderSelectionStrategy
{
    /// <summary>
    /// Unique name of this strategy (e.g., "health-score", "cost-based").
    /// </summary>
    string StrategyName { get; }

    /// <summary>
    /// Select the best provider from the given candidates.
    /// </summary>
    /// <param name="candidates">Provider health scores for all registered providers.</param>
    /// <returns>The name of the selected provider, or null if no suitable provider found.</returns>
    string? SelectProvider(IReadOnlyDictionary<string, ProviderHealthScore> candidates);
}
