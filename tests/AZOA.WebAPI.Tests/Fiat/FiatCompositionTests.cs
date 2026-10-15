// SPDX-License-Identifier: UNLICENSED
// Wave 4 join gate -- the WIRING of the fiat money path, as opposed to its behaviour.
//
// OnRampSagaTests and ReversalTests both construct PriceScheduleDeclaredRateStore and
// RiskHoldValueGate DIRECTLY, so they prove those classes behave and prove nothing at all about
// the registrations that are supposed to put them in the request path. That is the exact shape of
// this codebase's recurring defect (RUNBOOK §2): a control that exists, is tested, and is never
// reached.
//
// Both registrations are also unusually easy to get wrong, because neither is a plain Add: each
// REMOVES the descriptor it is displacing and re-registers it behind a marker type so the
// replacement can resolve its own predecessor without resolving itself. A mistake there does not
// fail the build and does not fail a behaviour test -- it either loses the fallback or stack-
// overflows on first resolution, in production, on the money path.
//
// So these tests resolve through a real ServiceProvider and assert the composed identity:
//   * the price schedule ends up deciding the value stamp, with the configured store kept as the
//     fallback rather than dropped;
//   * the reversal risk flag ends up wrapping the D46 gate, so a flagged account is refused;
//   * and every ordering guard actually throws, so a future Program.cs edit that reorders the
//     calls fails loudly instead of quietly disconnecting a control.

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Services.Analytics;
using AZOA.WebAPI.Services.Fiat;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using AZOA.WebAPI.Services.Fiat.Reversal;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SurrealForge.Client.Query;
using Xunit;

namespace AZOA.WebAPI.Tests.Fiat;

public class FiatCompositionTests
{
    // ── §rate-source — the schedule that sold the units also prices them ──────

    [Fact]
    public void AddOnRampValueRateSource_AfterAddValueRateSources_PutsTheScheduleInFront()
    {
        var services = BaseServices();
        services.AddValueRateSources(EmptyConfiguration());
        var configured = ResolvedDeclaredRateStore(services);
        configured.Should().NotBeNull(
            "AddValueRateSources must install a store for the on-ramp to displace");

        OnRampRegistration.AddFiatOnRamp(services);
        OnRampRegistration.AddOnRampValueRateSource(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Resolution is the assertion: a self-referential replacement would stack-overflow here
        // rather than return, which is precisely why this is resolved and not merely inspected.
        scope.ServiceProvider.GetRequiredService<ITenantDeclaredRateStore>()
            .Should().BeOfType<PriceScheduleDeclaredRateStore>(
                "the price that converted the money must also be the price the value stamp uses");
    }

    [Fact]
    public void AddOnRampValueRateSource_KeepsTheConfiguredStoreAsTheFallback()
    {
        var services = BaseServices();
        services.AddValueRateSources(EmptyConfiguration());
        var displaced = ResolvedDeclaredRateStore(services)!;

        OnRampRegistration.AddFiatOnRamp(services);
        OnRampRegistration.AddOnRampValueRateSource(services);

        // The displaced descriptor is not dropped: it is re-registered behind the marker so an
        // asset with no registered schedule (a project token priced from configuration) still
        // prices. Losing it would silently un-price every such asset.
        services.Should().Contain(
            descriptor => descriptor.ServiceType
                == typeof(OnRampRegistration.ConfiguredTenantDeclaredRateStoreMarker),
            "the configured store must survive as the fallback, not be discarded");

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var marker = scope.ServiceProvider
            .GetRequiredService<OnRampRegistration.ConfiguredTenantDeclaredRateStoreMarker>();

        marker.Inner.Should().NotBeNull().And.NotBeOfType<PriceScheduleDeclaredRateStore>(
            "the fallback must be the store that was displaced, never the replacement itself");
        marker.Inner.Should().BeOfType(displaced.GetType());
    }

    [Fact]
    public void AddOnRampValueRateSource_WithoutTheOnRamp_Throws()
    {
        var services = BaseServices();
        services.AddValueRateSources(EmptyConfiguration());

        var act = () => OnRampRegistration.AddOnRampValueRateSource(services);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddFiatOnRamp*");
    }

    [Fact]
    public void AddOnRampValueRateSource_WithoutAConfiguredStoreToCompose_Throws()
    {
        // The dangerous ordering: on-ramp first, rate sources never. Silently accepting it would
        // leave the node pricing nothing while every seam still looked registered.
        var services = BaseServices();
        OnRampRegistration.AddFiatOnRamp(services);

        var act = () => OnRampRegistration.AddOnRampValueRateSource(services);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddValueRateSources*");
    }

    [Fact]
    public void AddFiatOnRamp_WithoutTheValueGate_Throws()
    {
        // D46 gates every value action on a current identity approval, and buying units is a
        // value action. Registering the on-ramp ahead of the gate must not be possible.
        var services = BaseServices(includeValueGate: false);

        var act = () => OnRampRegistration.AddFiatOnRamp(services);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IRealValueKycGate*");
    }

    // ── §risk-flag — the reversal pause is a control, not a stored field ──────

    [Fact]
    public void AddReversalRiskValueGate_WrapsTheRegisteredValueGate()
    {
        var services = BaseServices();
        services.AddScoped<IFiatReversalLedger>(_ => Mock.Of<IFiatReversalLedger>());

        ReversalRegistration.AddReversalRiskValueGate(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IRealValueKycGate>()
            .Should().BeOfType<RiskHoldValueGate>(
                "without this the account flag is written and nothing ever reads it");
        scope.ServiceProvider
            .GetRequiredService<ReversalRegistration.InnerRealValueKycGateMarker>()
            .Inner.Should().NotBeOfType<RiskHoldValueGate>(
                "the wrapper must delegate to the gate it displaced, not to itself");
    }

    [Fact]
    public void AddReversalRiskValueGate_IsIdempotent()
    {
        // Applying it twice must not stack one wrapper on another: a doubled gate is a doubled
        // ledger read on every value action, and the second wrapper's inner is the first.
        var services = BaseServices();
        services.AddScoped<IFiatReversalLedger>(_ => Mock.Of<IFiatReversalLedger>());

        ReversalRegistration.AddReversalRiskValueGate(services);
        ReversalRegistration.AddReversalRiskValueGate(services);

        services.Count(descriptor => descriptor.ServiceType
                == typeof(ReversalRegistration.InnerRealValueKycGateMarker))
            .Should().Be(1);
    }

    [Fact]
    public void AddReversalRiskValueGate_WithNoGateToWrap_Throws()
    {
        var services = BaseServices(includeValueGate: false);
        services.AddScoped<IFiatReversalLedger>(_ => Mock.Of<IFiatReversalLedger>());

        var act = () => ReversalRegistration.AddReversalRiskValueGate(services);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IRealValueKycGate*");
    }

    [Fact]
    public void AddReversalRiskValueGate_WithoutTheReversalLedger_Throws()
    {
        var act = () => ReversalRegistration.AddReversalRiskValueGate(BaseServices());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddFiatReversalPipeline*");
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The collaborators the on-ramp and the reversal registrations check for, as mocks. Nothing
    /// here is exercised: these tests assert composition, and a mock satisfies both the guards'
    /// descriptor probe and the container's resolution.
    /// </summary>
    private static ServiceCollection BaseServices(bool includeValueGate = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(EmptyConfiguration());
        services.AddScoped(_ => Mock.Of<ISurrealExecutor>());
        services.AddScoped(_ => Mock.Of<IAllocationManager>());
        services.AddScoped(_ => Mock.Of<IPlatformTokenGenesis>());
        if (includeValueGate)
            services.AddScoped(_ => Mock.Of<IRealValueKycGate>());
        return services;
    }

    private static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    /// <summary>The declared-rate store currently registered, resolved once so its type is known.</summary>
    private static ITenantDeclaredRateStore? ResolvedDeclaredRateStore(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        return scope.ServiceProvider.GetService<ITenantDeclaredRateStore>();
    }
}
