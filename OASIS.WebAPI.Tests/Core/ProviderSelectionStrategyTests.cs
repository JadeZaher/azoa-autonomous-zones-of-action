using FluentAssertions;
using Microsoft.Extensions.Configuration;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Core.ProviderSelection;

namespace OASIS.WebAPI.Tests.Core;

public class ProviderSelectionStrategyTests
{
    [Fact]
    public void HealthScoreStrategy_ShouldPickHighestScore()
    {
        var strategy = new HealthScoreStrategy();
        var candidates = new Dictionary<string, ProviderHealthScore>
        {
            ["A"] = new() { ProviderName = "A", Score = 30, IsHealthy = true },
            ["B"] = new() { ProviderName = "B", Score = 80, IsHealthy = true },
            ["C"] = new() { ProviderName = "C", Score = 50, IsHealthy = true }
        };

        var result = strategy.SelectProvider(candidates);
        result.Should().Be("B");
    }

    [Fact]
    public void HealthScoreStrategy_ShouldExcludeUnhealthy()
    {
        var strategy = new HealthScoreStrategy();
        var candidates = new Dictionary<string, ProviderHealthScore>
        {
            ["A"] = new() { ProviderName = "A", Score = 100, IsHealthy = false },
            ["B"] = new() { ProviderName = "B", Score = 50, IsHealthy = true }
        };

        var result = strategy.SelectProvider(candidates);
        result.Should().Be("B");
    }

    [Fact]
    public void LowestLatencyStrategy_ShouldPickFastest()
    {
        var strategy = new LowestLatencyStrategy();
        var candidates = new Dictionary<string, ProviderHealthScore>
        {
            ["Slow"] = new() { ProviderName = "Slow", LastLatencyMs = 500, IsHealthy = true },
            ["Fast"] = new() { ProviderName = "Fast", LastLatencyMs = 10, IsHealthy = true }
        };

        var result = strategy.SelectProvider(candidates);
        result.Should().Be("Fast");
    }

    [Fact]
    public void RoundRobinStrategy_ShouldCycle()
    {
        var strategy = new RoundRobinStrategy();
        var candidates = new Dictionary<string, ProviderHealthScore>
        {
            ["A"] = new() { ProviderName = "A", IsHealthy = true },
            ["B"] = new() { ProviderName = "B", IsHealthy = true }
        };

        var first = strategy.SelectProvider(candidates);
        var second = strategy.SelectProvider(candidates);
        var third = strategy.SelectProvider(candidates);

        first.Should().BeOneOf("A", "B");
        second.Should().NotBe(first);
        third.Should().Be(first);
    }

    [Fact]
    public void WeightedStrategy_ShouldRespectWeights()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OASIS:ProviderWeights:Heavy"] = "90",
                ["OASIS:ProviderWeights:Light"] = "10"
            })
            .Build();

        var strategy = new WeightedStrategy(config);
        var candidates = new Dictionary<string, ProviderHealthScore>
        {
            ["Heavy"] = new() { ProviderName = "Heavy", IsHealthy = true },
            ["Light"] = new() { ProviderName = "Light", IsHealthy = true }
        };

        // With 90/10 weight, Heavy should be selected most of the time
        var heavyCount = 0;
        for (int i = 0; i < 100; i++)
        {
            if (strategy.SelectProvider(candidates) == "Heavy")
                heavyCount++;
        }

        heavyCount.Should().BeGreaterThan(70); // probabilistic, but should be heavily skewed
    }

    [Fact]
    public void StickySessionStrategy_ShouldStickToSameProvider()
    {
        var strategy = new StickySessionStrategy();
        var candidates = new Dictionary<string, ProviderHealthScore>
        {
            ["A"] = new() { ProviderName = "A", Score = 80, IsHealthy = true },
            ["B"] = new() { ProviderName = "B", Score = 90, IsHealthy = true }
        };

        strategy.SessionKey = "avatar-123";
        var first = strategy.SelectProvider(candidates);

        // Change scores — sticky should still return same provider
        candidates["A"] = new() { ProviderName = "A", Score = 10, IsHealthy = true };
        candidates["B"] = new() { ProviderName = "B", Score = 99, IsHealthy = true };

        var second = strategy.SelectProvider(candidates);

        second.Should().Be(first);
    }

    [Fact]
    public void StickySessionStrategy_WhenStickyUnhealthy_ShouldFallback()
    {
        var strategy = new StickySessionStrategy();
        var candidates = new Dictionary<string, ProviderHealthScore>
        {
            ["A"] = new() { ProviderName = "A", Score = 80, IsHealthy = true },
            ["B"] = new() { ProviderName = "B", Score = 90, IsHealthy = true }
        };

        strategy.SessionKey = "avatar-456";
        var first = strategy.SelectProvider(candidates);

        // Make sticky provider unhealthy
        candidates[first!] = new() { ProviderName = first!, Score = 0, IsHealthy = false };

        var second = strategy.SelectProvider(candidates);
        second.Should().NotBe(first);
    }

    [Fact]
    public void StickySessionStrategy_NoSessionKey_ShouldUseFallback()
    {
        var strategy = new StickySessionStrategy();
        var candidates = new Dictionary<string, ProviderHealthScore>
        {
            ["A"] = new() { ProviderName = "A", Score = 80, IsHealthy = true },
            ["B"] = new() { ProviderName = "B", Score = 90, IsHealthy = true }
        };

        strategy.SessionKey = null;
        var result = strategy.SelectProvider(candidates);
        result.Should().Be("B"); // fallback to HealthScore (highest score)
    }
}
