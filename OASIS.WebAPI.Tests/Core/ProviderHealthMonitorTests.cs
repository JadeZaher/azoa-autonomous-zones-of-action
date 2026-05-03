using FluentAssertions;
using OASIS.WebAPI.Core;

namespace OASIS.WebAPI.Tests.Core;

public class ProviderHealthMonitorTests
{
    private readonly ProviderHealthMonitor _monitor;

    public ProviderHealthMonitorTests()
    {
        _monitor = new ProviderHealthMonitor();
    }

    [Fact]
    public void RecordSuccess_ShouldCreateScore()
    {
        _monitor.RecordSuccess("EfStorage", 50);

        var scores = _monitor.GetScores();
        scores.Should().ContainKey("EfStorage");
        scores["EfStorage"].IsHealthy.Should().BeTrue();
        scores["EfStorage"].SuccessCount.Should().Be(1);
    }

    [Fact]
    public void RecordFailure_ShouldIncrementFailures()
    {
        _monitor.RecordFailure("EfStorage");

        var scores = _monitor.GetScores();
        scores["EfStorage"].FailureCount.Should().Be(1);
        scores["EfStorage"].ConsecutiveFailures.Should().Be(1);
    }

    [Fact]
    public void RecordFailure_FiveTimes_ShouldMarkUnhealthy()
    {
        for (int i = 0; i < 5; i++)
            _monitor.RecordFailure("EfStorage");

        var scores = _monitor.GetScores();
        scores["EfStorage"].IsHealthy.Should().BeFalse();
        scores["EfStorage"].Score.Should().Be(0);
    }

    [Fact]
    public void RecordSuccess_AfterFailures_ShouldResetConsecutiveFailures()
    {
        _monitor.RecordFailure("EfStorage");
        _monitor.RecordFailure("EfStorage");
        _monitor.RecordSuccess("EfStorage", 10);

        var scores = _monitor.GetScores();
        scores["EfStorage"].ConsecutiveFailures.Should().Be(0);
        scores["EfStorage"].IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void SelectBestProvider_HealthScore_ShouldPickHighest()
    {
        _monitor.RecordSuccess("A", 100);
        _monitor.RecordSuccess("A", 100);
        _monitor.RecordSuccess("B", 10);
        _monitor.RecordSuccess("B", 10);
        _monitor.RecordSuccess("B", 10);

        var best = _monitor.SelectBestProvider(DynamicProviderMode.HealthScore);
        best.Should().Be("B"); // B has lower latency, higher score
    }

    [Fact]
    public void SelectBestProvider_LowestLatency_ShouldPickFastest()
    {
        _monitor.RecordSuccess("Slow", 500);
        _monitor.RecordSuccess("Fast", 10);

        var best = _monitor.SelectBestProvider(DynamicProviderMode.LowestLatency);
        best.Should().Be("Fast");
    }

    [Fact]
    public void SelectBestProvider_RoundRobin_ShouldCycle()
    {
        _monitor.RecordSuccess("A", 10);
        _monitor.RecordSuccess("B", 10);

        var first = _monitor.SelectBestProvider(DynamicProviderMode.RoundRobin);
        var second = _monitor.SelectBestProvider(DynamicProviderMode.RoundRobin);
        var third = _monitor.SelectBestProvider(DynamicProviderMode.RoundRobin);

        first.Should().BeOneOf("A", "B");
        second.Should().BeOneOf("A", "B");
        third.Should().Be(first); // cycles back
    }

    [Fact]
    public void SelectBestProvider_Unhealthy_ShouldBeExcluded()
    {
        _monitor.RecordSuccess("Healthy", 10);
        for (int i = 0; i < 5; i++)
            _monitor.RecordFailure("Unhealthy");

        var best = _monitor.SelectBestProvider(DynamicProviderMode.HealthScore);
        best.Should().Be("Healthy");
    }

    [Fact]
    public void SelectBestProvider_AllUnhealthy_ShouldFallbackToAny()
    {
        for (int i = 0; i < 5; i++)
            _monitor.RecordFailure("A");

        var best = _monitor.SelectBestProvider(DynamicProviderMode.HealthScore);
        best.Should().Be("A"); // fallback to any provider
    }

    [Fact]
    public void MarkUnhealthy_ShouldSetIsHealthyFalse()
    {
        _monitor.RecordSuccess("A", 10);
        _monitor.MarkUnhealthy("A");

        var scores = _monitor.GetScores();
        scores["A"].IsHealthy.Should().BeFalse();
    }

    [Fact]
    public void MarkHealthy_ShouldRestoreProvider()
    {
        _monitor.MarkUnhealthy("A");
        _monitor.MarkHealthy("A");

        var scores = _monitor.GetScores();
        scores["A"].IsHealthy.Should().BeTrue();
        scores["A"].ConsecutiveFailures.Should().Be(0);
    }
}
