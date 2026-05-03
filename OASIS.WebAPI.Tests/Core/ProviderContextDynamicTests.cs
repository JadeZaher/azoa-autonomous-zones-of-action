using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Requests;

namespace OASIS.WebAPI.Tests.Core;

public class ProviderContextDynamicTests
{
    [Fact]
    public void Activate_DynamicHealthScore_ShouldSelectBestProvider()
    {
        var providerA = new Mock<IOASISStorageProvider>();
        providerA.Setup(p => p.ProviderName).Returns("EfStorage");
        var providerB = new Mock<IOASISStorageProvider>();
        providerB.Setup(p => p.ProviderName).Returns("InMemory");

        var monitor = new ProviderHealthMonitor();
        monitor.RecordSuccess("InMemory", 10);
        monitor.RecordSuccess("InMemory", 10);
        monitor.RecordSuccess("InMemory", 10);
        monitor.RecordFailure("EfStorage");
        monitor.RecordFailure("EfStorage");
        monitor.RecordFailure("EfStorage");
        monitor.RecordFailure("EfStorage");
        monitor.RecordFailure("EfStorage"); // now unhealthy

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OASIS:DynamicProviderMode"] = "HealthScore"
            })
            .Build();

        var context = new ProviderContext(new[] { providerA.Object, providerB.Object }, config, monitor);
        var response = context.Activate();

        response.IsError.Should().BeFalse();
        context.CurrentProvider.ProviderName.Should().Be("InMemory");
    }

    [Fact]
    public void Activate_DynamicLowestLatency_ShouldSelectFastest()
    {
        var providerA = new Mock<IOASISStorageProvider>();
        providerA.Setup(p => p.ProviderName).Returns("Slow");
        var providerB = new Mock<IOASISStorageProvider>();
        providerB.Setup(p => p.ProviderName).Returns("Fast");

        var monitor = new ProviderHealthMonitor();
        monitor.RecordSuccess("Slow", 500);
        monitor.RecordSuccess("Fast", 10);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OASIS:DynamicProviderMode"] = "LowestLatency"
            })
            .Build();

        var context = new ProviderContext(new[] { providerA.Object, providerB.Object }, config, monitor);
        var response = context.Activate();

        response.IsError.Should().BeFalse();
        context.CurrentProvider.ProviderName.Should().Be("Fast");
    }

    [Fact]
    public void Activate_DynamicOff_ShouldUseConfigDefault()
    {
        var providerA = new Mock<IOASISStorageProvider>();
        providerA.Setup(p => p.ProviderName).Returns("EfStorage");
        var providerB = new Mock<IOASISStorageProvider>();
        providerB.Setup(p => p.ProviderName).Returns("InMemory");

        var monitor = new ProviderHealthMonitor();
        monitor.RecordSuccess("InMemory", 10); // would be selected if dynamic on

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OASIS:DefaultProvider"] = "EfStorage",
                ["OASIS:DynamicProviderMode"] = "Off"
            })
            .Build();

        var context = new ProviderContext(new[] { providerA.Object, providerB.Object }, config, monitor);
        var response = context.Activate();

        response.IsError.Should().BeFalse();
        context.CurrentProvider.ProviderName.Should().Be("EfStorage");
    }

    [Fact]
    public void Activate_DynamicWithExplicitRequest_ShouldOverrideDynamic()
    {
        var providerA = new Mock<IOASISStorageProvider>();
        providerA.Setup(p => p.ProviderName).Returns("EfStorage");
        var providerB = new Mock<IOASISStorageProvider>();
        providerB.Setup(p => p.ProviderName).Returns("InMemory");

        var monitor = new ProviderHealthMonitor();
        monitor.RecordSuccess("InMemory", 10); // dynamic would pick this

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OASIS:DynamicProviderMode"] = "HealthScore"
            })
            .Build();

        var context = new ProviderContext(new[] { providerA.Object, providerB.Object }, config, monitor);
        var request = new OASISRequest { ProviderType = ProviderType.Default };
        var response = context.Activate(request);

        // ProviderType.Default with dynamic on → should use dynamic selection
        response.IsError.Should().BeFalse();
        context.CurrentProvider.ProviderName.Should().Be("InMemory");
    }

    [Fact]
    public void RecordSuccess_ShouldUpdateHealthMonitor()
    {
        var provider = new Mock<IOASISStorageProvider>();
        provider.Setup(p => p.ProviderName).Returns("TestProvider");

        var monitor = new ProviderHealthMonitor();
        var config = new ConfigurationBuilder().Build();
        var context = new ProviderContext(new[] { provider.Object }, config, monitor);

        context.Activate();
        context.RecordSuccess(25.5);

        var scores = monitor.GetScores();
        scores["TestProvider"].LastLatencyMs.Should().Be(25.5);
        scores["TestProvider"].SuccessCount.Should().Be(1);
    }

    [Fact]
    public void RecordFailure_ShouldUpdateHealthMonitor()
    {
        var provider = new Mock<IOASISStorageProvider>();
        provider.Setup(p => p.ProviderName).Returns("TestProvider");

        var monitor = new ProviderHealthMonitor();
        var config = new ConfigurationBuilder().Build();
        var context = new ProviderContext(new[] { provider.Object }, config, monitor);

        context.Activate();
        context.RecordFailure();

        var scores = monitor.GetScores();
        scores["TestProvider"].FailureCount.Should().Be(1);
        scores["TestProvider"].ConsecutiveFailures.Should().Be(1);
    }
}
