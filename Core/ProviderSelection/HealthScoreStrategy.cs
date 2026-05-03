using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;

namespace OASIS.WebAPI.Core.ProviderSelection;

public class HealthScoreStrategy : IProviderSelectionStrategy
{
    public string StrategyName => "health-score";

    public string? SelectProvider(IReadOnlyDictionary<string, ProviderHealthScore> candidates)
    {
        return candidates.Values
            .Where(s => s.IsHealthy)
            .OrderByDescending(s => s.Score)
            .FirstOrDefault()
            ?.ProviderName;
    }
}
