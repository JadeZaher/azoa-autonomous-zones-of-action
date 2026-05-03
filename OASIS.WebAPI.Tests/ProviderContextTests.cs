using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests;

public class ProviderContextTests
{
    private readonly Mock<IOASISStorageProvider> _providerA;
    private readonly Mock<IOASISStorageProvider> _providerB;
    private readonly ProviderContext _context;

    public ProviderContextTests()
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
    public void Activate_WithDefaultRequest_ShouldSelectDefaultProvider()
    {
        var response = _context.Activate();

        response.IsError.Should().BeFalse();
        _context.CurrentProvider.ProviderName.Should().Be("PostgreSQL");
        _context.AllActiveProviders.Should().ContainSingle();
    }

    [Fact]
    public void Activate_WithSpecificProviderType_ShouldSelectMatchingProvider()
    {
        var request = new OASISRequest { ProviderType = ProviderType.Ethereum };
        // No provider named "Ethereum", falls back to first available
        var response = _context.Activate(request);

        response.IsError.Should().BeFalse();
        _context.CurrentProvider.ProviderName.Should().Be("PostgreSQL");
    }

    [Fact]
    public void Activate_WithNonMatchingProviderType_ShouldFallbackToFirst()
    {
        var request = new OASISRequest { ProviderType = ProviderType.MongoDB };
        var response = _context.Activate(request);

        response.IsError.Should().BeFalse();
        _context.CurrentProvider.ProviderName.Should().Be("PostgreSQL");
    }

    [Fact]
    public void Activate_WithFailOver_ShouldIncludeExtraProviders()
    {
        var request = new OASISRequest { AutoFailOverMode = AutoFailOverMode.On };
        var response = _context.Activate(request);

        response.IsError.Should().BeFalse();
        _context.AllActiveProviders.Should().HaveCount(2);
    }

    [Fact]
    public void Activate_WithReplication_ShouldIncludeExtraProviders()
    {
        var request = new OASISRequest { AutoReplicationMode = AutoReplicationMode.On };
        var response = _context.Activate(request);

        response.IsError.Should().BeFalse();
        _context.AllActiveProviders.Should().HaveCount(2);
    }

    [Fact]
    public void Activate_WithNoProviders_ShouldReturnError()
    {
        var config = new ConfigurationBuilder().Build();
        var emptyContext = new ProviderContext(Array.Empty<IOASISStorageProvider>(), config, null);

        var response = emptyContext.Activate();

        response.IsError.Should().BeTrue();
        response.Message.Should().Contain("No storage provider available");
    }

    [Fact]
    public void Activate_WithNullRequest_ShouldUseDefaults()
    {
        var response = _context.Activate(null);

        response.IsError.Should().BeFalse();
        _context.CurrentProvider.ProviderName.Should().Be("PostgreSQL");
    }
}
