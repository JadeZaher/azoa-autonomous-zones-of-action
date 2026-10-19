using System.Reflection;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using AZOA.WebAPI.Services.Fiat.Reversal;
using AZOA.WebAPI.Services.Fungible;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Services.NodeAdmin;
using AZOA.WebAPI.Services.Wormhole;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
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
    public void Value_access_checks_go_through_the_reversal_risk_hold()
    {
        using var scope = _factory.Services.CreateScope();

        var access = scope.ServiceProvider.GetRequiredService<IValueAccessService>();
        var gate = access.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(field => field.FieldType == typeof(IRealValueKycGate)).GetValue(access);

        access.Should().BeOfType<ValueAccessService>();
        gate.Should().BeOfType<RiskHoldValueGate>("hold, exchange, fungible, NFT and deployment paths all ask IValueAccessService (RV-X X-2)");
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

    [Fact]
    public void Every_node_admin_route_grant_resolves_against_the_live_endpoint_table_through_the_runtime_lookup()
    {
        // L-2: the gate tests key routes from reflection; this proves the runtime key (RoutePattern.RawText) agrees, row for row.
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string>();
        foreach (var endpoint in Services().GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                     .Where(NodeAdminRoutePermissions.IsOperatorEndpoint))
        {
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            {
                var key = NodeAdminRoutePermissions.Key(method, endpoint.RoutePattern.RawText);
                if (NodeAdminRoutePermissions.TryResolve(endpoint, method, out _)) resolved.Add(key);
                else unresolved.Add(key);
            }
        }

        unresolved.Should().BeEmpty("every live operator-policy endpoint must resolve to a grant at runtime (unresolved = node admins denied)");
        NodeAdminRoutePermissions.Map.Keys.Except(resolved, StringComparer.Ordinal).Should()
            .BeEmpty("every grant must be reachable from a live endpoint through the same lookup the policies use");
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
