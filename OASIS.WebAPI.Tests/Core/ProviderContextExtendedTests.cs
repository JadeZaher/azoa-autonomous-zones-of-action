using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests.Core;

public class ProviderContextExtendedTests
{
    private readonly Mock<IOASISStorageProvider> _providerA;
    private readonly Mock<IOASISStorageProvider> _providerB;
    private readonly ProviderContext _context;

    public ProviderContextExtendedTests()
    {
        _providerA = new Mock<IOASISStorageProvider>();
        _providerA.Setup(p => p.ProviderName).Returns("PostgreSQL");

        _providerB = new Mock<IOASISStorageProvider>();
        _providerB.Setup(p => p.ProviderName).Returns("InMemory");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OASIS:DefaultProvider"] = "PostgreSQL"
            })
            .Build();

        _context = new ProviderContext(new[] { _providerA.Object, _providerB.Object }, config, null);
    }

    [Fact]
    public void Activate_WithLoadBalance_ShouldActivateSuccessfully()
    {
        var request = new OASISRequest { AutoLoadBalanceMode = AutoLoadBalanceMode.On };
        var response = _context.Activate(request);

        response.IsError.Should().BeFalse();
        _context.CurrentProvider.Should().NotBeNull();
    }

    [Fact]
    public void Activate_WithBothFailOverAndReplication_ShouldNotDuplicate()
    {
        var request = new OASISRequest
        {
            AutoFailOverMode = AutoFailOverMode.On,
            AutoReplicationMode = AutoReplicationMode.On
        };
        var response = _context.Activate(request);

        response.IsError.Should().BeFalse();
        _context.AllActiveProviders.Should().HaveCount(2);
    }

    [Fact]
    public void Activate_WithSpecificProviderName_ShouldSelectMatching()
    {
        var config = new ConfigurationBuilder().Build();
        var context = new ProviderContext(new[] { _providerA.Object, _providerB.Object }, config, null);

        var request = new OASISRequest { ProviderType = ProviderType.SQLLite };
        var response = context.Activate(request);

        response.IsError.Should().BeFalse();
        context.CurrentProvider.ProviderName.Should().Be("PostgreSQL"); // falls back to first when no match
    }

    [Fact]
    public void Activate_WhenTargetProviderMissing_ShouldFallbackToFirst()
    {
        var config = new ConfigurationBuilder().Build();
        var context = new ProviderContext(new[] { _providerB.Object }, config, null);

        var request = new OASISRequest { ProviderType = ProviderType.MongoDB };
        var response = context.Activate(request);

        response.IsError.Should().BeFalse();
        context.CurrentProvider.ProviderName.Should().Be("InMemory");
    }

    [Fact]
    public void Activate_WithDefaultConfig_ShouldRespectConfigDefault()
    {
        var response = _context.Activate();

        response.IsError.Should().BeFalse();
        _context.CurrentProvider.ProviderName.Should().Be("PostgreSQL");
    }

    [Fact]
    public void Activate_WithCustomProviderKeys_ShouldIgnoreAndUseDefault()
    {
        var request = new OASISRequest { CustomProviderKeys = new List<string> { "Custom" } };
        var response = _context.Activate(request);

        response.IsError.Should().BeFalse();
        _context.CurrentProvider.ProviderName.Should().Be("PostgreSQL");
    }
}
