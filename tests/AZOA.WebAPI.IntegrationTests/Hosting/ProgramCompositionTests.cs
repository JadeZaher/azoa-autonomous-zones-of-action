using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Services.Fiat.Reversal;
using AZOA.WebAPI.Services.Fungible;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AZOA.WebAPI.IntegrationTests.Hosting;

/// <summary>Builds the real Development composition root; guards the Program.cs ordering rule in root AGENTS.md §Program.cs registration order.</summary>
public sealed class ProgramCompositionTests : IClassFixture<ProgramCompositionTests.DevelopmentCompositionFactory>
{
    private readonly DevelopmentCompositionFactory _factory;

    public ProgramCompositionTests(DevelopmentCompositionFactory factory) => _factory = factory;

    [Fact]
    public void Development_host_builds_with_validate_on_build_and_validate_scopes()
    {
        var build = () => _factory.Services;

        build.Should().NotThrow("every registration, including hosted workers, must be constructible from the container");
    }

    [Fact]
    public void Kyc_value_gate_is_the_reversal_risk_hold_wrapping_the_kyc_gate()
    {
        using var scope = _factory.Services.CreateScope();

        var gate = scope.ServiceProvider.GetRequiredService<IRealValueKycGate>();
        var inner = scope.ServiceProvider.GetRequiredService<ReversalRegistration.InnerRealValueKycGateMarker>().Inner;

        gate.Should().BeOfType<RiskHoldValueGate>("a KYC gate registered after AddReversalRiskValueGate would bypass the reversal risk hold");
        inner.Should().NotBeOfType<RiskHoldValueGate>();
    }

    [Fact]
    public void Wormhole_adapter_and_fungible_creation_lifecycle_resolve()
    {
        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        services.Invoking(provider => provider.GetRequiredService<IWormholeAdapter>()).Should().NotThrow("WormholeProtocol depends on it");
        services.Invoking(provider => provider.GetRequiredService<IFungibleCreationStore>()).Should().NotThrow();
        services.Invoking(provider => provider.GetRequiredService<IFungibleCreationService>()).Should().NotThrow();

        services.GetServices<IHostedService>().Should().ContainSingle(service => service is FungibleCreationRecoveryWorker);
    }

    /// <summary>The shared test host switched to the Development environment with full container validation.</summary>
    public sealed class DevelopmentCompositionFactory : AZOATestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Development");
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });
        }
    }
}
