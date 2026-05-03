using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;

namespace OASIS.WebAPI.Core.ProviderSelection;

/// <summary>
/// Weighted random selection. Configure weights in app settings:
/// OASIS:ProviderWeights:EfStorage = 70
/// OASIS:ProviderWeights:InMemory = 30
/// </summary>
public class WeightedStrategy : IProviderSelectionStrategy
{
    public string StrategyName => "weighted";

    private readonly IConfiguration _config;
    private readonly Random _random = new();

    public WeightedStrategy(IConfiguration config)
    {
        _config = config;
    }

    public string? SelectProvider(IReadOnlyDictionary<string, ProviderHealthScore> candidates)
    {
        var healthy = candidates.Values.Where(s => s.IsHealthy).ToList();
        if (!healthy.Any()) return null;

        // Build weight map from config, defaulting to 100 for each
        var weights = new List<(string Name, int Weight)>();
        foreach (var candidate in healthy)
        {
            var weight = _config.GetValue<int?>($"OASIS:ProviderWeights:{candidate.ProviderName}") ?? 100;
            weights.Add((candidate.ProviderName, weight));
        }

        var totalWeight = weights.Sum(w => w.Weight);
        var pick = _random.Next(totalWeight);
        var cumulative = 0;

        foreach (var (name, weight) in weights)
        {
            cumulative += weight;
            if (pick < cumulative)
                return name;
        }

        return weights.Last().Name;
    }
}
