using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;

namespace OASIS.WebAPI.Core.ProviderSelection;

public class RoundRobinStrategy : IProviderSelectionStrategy
{
    public string StrategyName => "round-robin";

    private readonly object _lock = new();
    private int _index;

    public string? SelectProvider(IReadOnlyDictionary<string, ProviderHealthScore> candidates)
    {
        var healthy = candidates.Values.Where(s => s.IsHealthy).ToList();
        if (!healthy.Any()) return null;

        lock (_lock)
        {
            var selected = healthy[_index % healthy.Count];
            _index++;
            return selected.ProviderName;
        }
    }
}
