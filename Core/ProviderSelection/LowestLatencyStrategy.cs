using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;

namespace OASIS.WebAPI.Core.ProviderSelection;

public class LowestLatencyStrategy : IProviderSelectionStrategy
{
    public string StrategyName => "lowest-latency";

    public string? SelectProvider(IReadOnlyDictionary<string, ProviderHealthScore> candidates)
    {
        return candidates.Values
            .Where(s => s.IsHealthy)
            .OrderBy(s => s.LastLatencyMs)
            .FirstOrDefault()
            ?.ProviderName;
    }
}
