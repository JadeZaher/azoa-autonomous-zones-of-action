// SPDX-License-Identifier: UNLICENSED
//
// OI-45, at the right granularity. The credit-capability probe runs in TWO places and they are
// asking different questions. At BOOT there is no payment and no tenant, so "has ANY scope on this
// node registered a price" is the honest question. At the CHECKOUT COUNTER the tenant is in hand
// and the settlement handler's precedence is tenant-first / node-second — so a node-wide question
// there ADMITS the very checkout the claim says is now refused: on a node where only tenant U has
// registered a price, a checkout for tenant T passed the probe, the card was charged, and the lot
// parked Stuck.
//
// Every assertion is about what the probe concludes from stores it did not write.

using System.Numerics;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Fiat;

public sealed class FiatCreditCapabilityProbeTenantScopeTests
{
    private const string ChainType = "Algorand";
    private const string AssetId = "742000001";
    private const string PartitionName = "on-ramp";
    private const string ReserveAddress = "RESERVEADDRESSNOTAREALONE";

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid PayingTenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid OtherTenant = Guid.Parse("99999999-8888-7777-6666-555555555555");

    // ── The checkout the node-wide question let through ──────────────────────

    [Fact]
    public async Task A_checkout_for_a_tenant_with_no_price_is_refused_even_when_another_tenant_has_one()
    {
        // The exact OI-45 outcome: only tenant U has registered a price. Asked node-wide, the
        // answer is "yes, somebody has one" and tenant T's card is charged for a lot that then
        // parks Stuck because the settlement handler finds no price for T.
        var services = BuildProvider(pricedScopes: [PriceScheduleScope.For(OtherTenant)]);

        var capability = await FiatCreditCapabilityProbe.ProbeAsync(services, Now, PayingTenant);

        capability.CanCredit.Should().BeFalse(
            "the price that would settle THIS payment does not exist, and the probe runs where "
            + "the tenant is in hand");
        capability.Detail.Should().Contain("this tenant has registered no price");
    }

    [Fact]
    public async Task A_checkout_for_a_tenant_that_registered_its_own_price_is_admitted()
    {
        var services = BuildProvider(pricedScopes: [PriceScheduleScope.For(PayingTenant)]);

        var capability = await FiatCreditCapabilityProbe.ProbeAsync(services, Now, PayingTenant);

        capability.CanCredit.Should().BeTrue(capability.Detail);
    }

    [Fact]
    public async Task The_node_default_stands_in_only_where_the_operator_allowed_it()
    {
        // Substituting the operator's price for a tenant's is an economic decision, and the probe
        // must make it exactly where OnRampSettlementHandler.ResolveScheduleAsync makes it — a
        // probe with a WIDER rule than the settlement path passes a checkout that then parks.
        var refused = BuildProvider(
            pricedScopes: [PriceScheduleScope.Node], allowNodeDefaultFallback: false);
        var allowed = BuildProvider(
            pricedScopes: [PriceScheduleScope.Node], allowNodeDefaultFallback: true);

        (await FiatCreditCapabilityProbe.ProbeAsync(refused, Now, PayingTenant))
            .CanCredit.Should().BeFalse("the operator has not said its price may stand in");

        (await FiatCreditCapabilityProbe.ProbeAsync(allowed, Now, PayingTenant))
            .CanCredit.Should().BeTrue("the operator has said it may");
    }

    [Fact]
    public async Task A_tenant_that_retired_its_price_does_not_fall_through_to_the_node_default()
    {
        // A retired latest version means "we have stopped selling units", not "use the operator's
        // price instead" — falling through would resell at a price this tenant is not offering.
        var services = BuildProvider(
            pricedScopes: [PriceScheduleScope.Node],
            retiredScopes: [PriceScheduleScope.For(PayingTenant)],
            allowNodeDefaultFallback: true);

        var capability = await FiatCreditCapabilityProbe.ProbeAsync(services, Now, PayingTenant);

        capability.CanCredit.Should().BeFalse();
        capability.Detail.Should().Contain("retired or out of date");
    }

    // ── Boot keeps the boot-shaped question ──────────────────────────────────

    [Fact]
    public async Task Boot_asks_the_node_wide_question_because_it_has_no_tenant_to_ask_about()
    {
        var services = BuildProvider(pricedScopes: [PriceScheduleScope.For(OtherTenant)]);

        var capability = await FiatCreditCapabilityProbe.ProbeAsync(services, Now, tenantId: null);

        capability.CanCredit.Should().BeTrue(
            "one tenant holding a current price means SOME payment on this node can be converted, "
            + "which is all a boot guard can honestly assert");
    }

    [Fact]
    public async Task Boot_still_refuses_a_node_where_nobody_has_registered_a_price()
    {
        var services = BuildProvider(pricedScopes: []);

        var capability = await FiatCreditCapabilityProbe.ProbeAsync(services, Now, tenantId: null);

        capability.CanCredit.Should().BeFalse();
        capability.Detail.Should().Contain("no current price is registered");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static RegisteredPriceSchedule Price(string scopeKey, string status) => new(
        RecordId: $"tenant_price_schedule:{scopeKey}_1",
        ScopeKey: scopeKey,
        TenantId: null,
        ChainType: ChainType,
        AssetId: AssetId,
        CurrencyCode: PriceScheduleLimits.SupportedCurrencyCode,
        Version: 1,
        UnitsPerMinorUnit: 100m,
        Source: "operator",
        Note: null,
        Status: status,
        DeclaredAt: Now - TimeSpan.FromDays(1),
        RegisteredAt: Now - TimeSpan.FromDays(1));

    private static PlatformTokenState PlatformUnit() => new(
        new PlatformTokenDefinition(
            ChainType: ChainType,
            Network: "Mainnet",
            AssetId: AssetId,
            Name: PlatformTokenGenesisDefaults.Name,
            UnitName: PlatformTokenGenesisDefaults.UnitName,
            Decimals: PlatformTokenGenesisDefaults.Decimals,
            TotalSupply: new BigInteger(PlatformTokenGenesisDefaults.TotalBaseUnits),
            ReserveAddress: ReserveAddress,
            ActivatedAt: Now - TimeSpan.FromDays(30)),
        new PlatformTokenSupply(
            Total: new BigInteger(PlatformTokenGenesisDefaults.TotalBaseUnits),
            Circulating: BigInteger.Zero,
            Held: BigInteger.Zero,
            InReserve: new BigInteger(PlatformTokenGenesisDefaults.TotalBaseUnits)));

    /// <summary>
    /// A container with a declared, non-exhausted on-ramp partition — so the ONLY thing any of
    /// these tests can turn on is the price question.
    /// </summary>
    private static ServiceProvider BuildProvider(
        string[] pricedScopes,
        string[]? retiredScopes = null,
        bool allowNodeDefaultFallback = false)
    {
        var active = new HashSet<string>(pricedScopes, StringComparer.Ordinal);
        var retired = new HashSet<string>(retiredScopes ?? [], StringComparer.Ordinal);

        var genesis = new Mock<IPlatformTokenGenesis>();
        genesis
            .Setup(store => store.GetStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<PlatformTokenState>.Success(PlatformUnit()));

        var reserve = new Mock<IPlatformReserve>();
        reserve
            .Setup(store => store.GetLedgerAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<PlatformReserveLedger>.Success(new PlatformReserveLedger
            {
                Id = PlatformReserveLedger.RecordIdFor(ChainType, "Mainnet", AssetId),
                AssetId = AssetId,
                Chain = ChainType,
                Network = "Mainnet",
                ReserveAddress = ReserveAddress,
                TotalSupply = "2000000000",
                Partitions = ReservePartitionCodec.Encode(
                    new Dictionary<string, ReservePartitionCounters>(StringComparer.Ordinal)
                    {
                        [PartitionName] =
                            ReservePartitionCounters.Declaring(new BigInteger(1_000_000)),
                    }),
                Version = 1,
            }));

        var schedules = new Mock<ITenantPriceScheduleStore>();

        // The per-scope read the settlement handler uses, and the node-wide read boot uses. Both
        // are served from ONE fixture so the two questions cannot be answered from different data.
        schedules
            .Setup(store => store.GetLatestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string scopeKey, string _, string _, CancellationToken _) =>
                AZOAResult<RegisteredPriceSchedule?>.Success(
                    retired.Contains(scopeKey) ? Price(scopeKey, PriceScheduleStatuses.Retired)
                    : active.Contains(scopeKey) ? Price(scopeKey, PriceScheduleStatuses.Active)
                    : null));

        schedules
            .Setup(store => store.ListLatestPerScopeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>.Success(
                active.Select(scope => Price(scope, PriceScheduleStatuses.Active))
                    .Concat(retired.Select(scope => Price(scope, PriceScheduleStatuses.Retired)))
                    .ToList()));

        var services = new ServiceCollection();
        services.AddSingleton(new OnRampOptions
        {
            ReservePartition = PartitionName,
            AllowNodeDefaultScheduleFallback = allowNodeDefaultFallback,
        });
        services.AddScoped(_ => genesis.Object);
        services.AddScoped(_ => reserve.Object);
        services.AddScoped(_ => schedules.Object);

        return services.BuildServiceProvider();
    }
}
