using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using AZOA.WebAPI.Services.Fiat.Reversal;
using AZOA.WebAPI.Services.Fungible;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Services.Wormhole;
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

    /// <summary>Host start runs the Program.cs post-build guards; a DI cycle hidden behind factory lambdas deadlocks there, so bound it.</summary>
    private IServiceProvider Services()
    {
        var start = Task.Run(() => _factory.Services);
        if (!start.Wait(TimeSpan.FromSeconds(90)))
            throw new TimeoutException("The Development host did not finish starting in 90 s: a scoped/singleton cycle behind a factory registration recurses until DI's stack guard deadlocks (see Hosting/AGENTS.md).");
        return start.Result;
    }

    [Fact]
    public void Development_host_builds_with_validate_on_build_and_validate_scopes()
    {
        var build = () => Services();

        build.Should().NotThrow("every registration, including hosted workers, must be constructible from the container");
    }

    [Fact]
    public void Round_four_recovery_workers_are_registered_and_resolve()
    {
        var workers = Services().GetServices<IHostedService>().ToList();

        workers.Should().ContainSingle(service => service is NodeFeeSettlementRecoveryWorker, "node-fee settlement recovery must run (LEDGER identity liveness)");
        workers.Should().ContainSingle(service => service is HostedOutcomePollWorker, "Identomat outcomes are polled, not only called back");
        workers.Should().ContainSingle(service => service is TenantPayoutSweepWorker, "stranded D-8 debits are swept");
        workers.Should().ContainSingle(service => service is WttStageKeyReadinessWorker, "WTT stage-key readiness degrades instead of failing open");
    }

    [Fact]
    public void Kyc_value_gate_is_the_reversal_risk_hold_wrapping_the_kyc_gate()
    {
        using var scope = Services().CreateScope();

        var gate = scope.ServiceProvider.GetRequiredService<IRealValueKycGate>();
        var inner = scope.ServiceProvider.GetRequiredService<ReversalRegistration.InnerRealValueKycGateMarker>().Inner;

        gate.Should().BeOfType<RiskHoldValueGate>("a KYC gate registered after AddReversalRiskValueGate would bypass the reversal risk hold");
        inner.Should().NotBeOfType<RiskHoldValueGate>();
    }

    [Fact]
    public void Wormhole_adapter_and_fungible_creation_lifecycle_resolve()
    {
        using var scope = Services().CreateScope();
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
            // Development refuses to boot without the host-secret-store values a developer's .env supplies.
            // UseSetting, not ConfigureAppConfiguration: minimal hosting reads these before that delegate runs.
            builder.UseSetting("Fiat:Test:SecretKey", "sk_test_composition_root_only");
            builder.UseSetting("Fiat:Test:WebhookSecret", "whsec_composition_root_only");
            builder.UseSetting("Fiat:Test:ConnectWebhookSecret", "whsec_composition_root_connect_only");
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });
        }
    }
}
