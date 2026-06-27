using FluentAssertions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Blockchain;

namespace AZOA.WebAPI.Tests.Core;

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

    // Deleted in Mission B: the five SelectBestProvider_* tests. Provider
    // selection (DynamicProviderMode + strategies) was removed — single-provider
    // reality. The monitor is retained only to feed /health via GetScores();
    // the record/score/mark coverage below is the meaningful surface.

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
