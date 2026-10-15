// SPDX-License-Identifier: UNLICENSED
// L1.6 / OI-29 -- the live-boot guard proves CAPABILITY, not registration.
//
// FiatRailStartup.Validate only ever proved that an IFiatSettlementHandler TYPE was in the
// container, while docs/FIAT-RAIL.md described that as "a node cannot charge a real card while
// nothing is able to credit it" -- and then, four lines later, listed the two things the node
// still needed before it could credit anything. This suite pins the second guard: the two facts
// that actually decide creditability are ROWS, not settings -- a declared, non-exhausted on-ramp
// partition on the platform unit's reserve ledger, and a current registered price for that unit --
// and a live boot missing either is refused.
//
// Every fixture below is a store the guard READS. Nothing here asserts a property of its own
// fixture: each assertion is about what GuardLiveCapabilityAsync does with a reserve ledger row
// and a price-schedule result it did not write.

using System.Numerics;
using System.Text.Json;
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

public class FiatRailStartupCapabilityTests
{
    private const string ChainType = "Algorand";
    private const string AssetId = "742000001";
    private const string PartitionName = "on-ramp";
    private const string ReserveAddress = "RESERVEADDRESSNOTAREALONE";

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    // ── The plan's own test: a registered handler is not a creditable node ────

    [Fact]
    public async Task A_live_node_whose_on_ramp_partition_is_not_declared_refuses_to_boot()
    {
        // The reserve ledger row exists and carries an "investor" partition -- but nothing has
        // been declared for the on-ramp, so every issuance from a cleared payment would be
        // refused by the reserve after the card had already been charged.
        var services = BuildProvider(
            partitions: new Dictionary<string, ReservePartitionCounters>
            {
                ["investor"] = ReservePartitionCounters.Declaring(new BigInteger(1_000_000)),
            },
            schedules: new[] { ActivePrice() });

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*on-ramp*not declared*");
    }

    [Fact]
    public async Task A_live_node_with_a_declared_partition_and_a_current_price_boots()
    {
        var services = BuildProvider(DeclaredOnRamp(), new[] { ActivePrice() });

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        await act.Should().NotThrowAsync();
    }

    // ── The partition half ───────────────────────────────────────────────────

    [Fact]
    public async Task A_blank_partition_setting_resolves_to_no_partition_and_refuses_to_boot()
    {
        // OnRampOptions is bound with .Get<T>(), so Fiat__OnRamp__ReservePartition="" REPLACES the
        // "on-ramp" C# default rather than leaving it -- the shape a deploy produces most easily.
        // A default is only a control if the code resolves the empty case, so blank must resolve
        // to the NARROWEST answer (no partition), never back to the default.
        var services = BuildProvider(
            DeclaredOnRamp(), new[] { ActivePrice() }, configuredPartition: string.Empty);

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Fiat:OnRamp:ReservePartition is blank*");
    }

    [Fact]
    public async Task A_declared_but_exhausted_partition_refuses_to_boot()
    {
        // Declared 1000, issued 1000: the reserve refuses the next draw exactly as it would for an
        // undeclared partition, so presence alone is not the capability.
        var services = BuildProvider(
            new Dictionary<string, ReservePartitionCounters>
            {
                [PartitionName] = new(
                    Declared: new BigInteger(1000),
                    Issued: new BigInteger(1000),
                    Held: BigInteger.Zero,
                    Returned: BigInteger.Zero),
            },
            new[] { ActivePrice() });

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*no units left to draw*");
    }

    [Fact]
    public async Task A_partition_name_is_matched_the_way_the_reserve_normalises_it()
    {
        // The reserve lower-cases and trims every partition name it stores. An operator who wrote
        // "On-Ramp" in configuration has declared the same partition, and refusing that boot would
        // be a false alarm on a node that can credit perfectly well.
        var services = BuildProvider(
            DeclaredOnRamp(), new[] { ActivePrice() }, configuredPartition: "  On-Ramp  ");

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_unreadable_partition_blob_refuses_to_boot_rather_than_reading_as_empty()
    {
        var services = BuildProvider(
            DeclaredOnRamp(),
            new[] { ActivePrice() },
            partitionsOverride: JsonSerializer.SerializeToElement("this is not an object"));

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*could not be read*");
    }

    [Fact]
    public async Task A_platform_unit_with_no_reserve_ledger_row_refuses_to_boot()
    {
        var services = BuildProvider(
            DeclaredOnRamp(),
            new[] { ActivePrice() },
            ledgerResult: AZOAResult<PlatformReserveLedger>.Failure(
                $"No reserve ledger row exists for asset '{AssetId}' on '{ChainType}'."));

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*no reserve ledger row*");
    }

    [Fact]
    public async Task A_node_that_has_not_run_genesis_refuses_to_boot()
    {
        var services = BuildProvider(
            DeclaredOnRamp(),
            new[] { ActivePrice() },
            tokenStateResult: AZOAResult<PlatformTokenState>.Failure(
                "No platform unit has been created on this network yet."));

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*platform unit could not be resolved*");
    }

    // ── The price half (OI-45) ───────────────────────────────────────────────

    [Fact]
    public async Task A_live_node_with_no_registered_price_refuses_to_boot()
    {
        var services = BuildProvider(DeclaredOnRamp(), Array.Empty<RegisteredPriceSchedule>());

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*no current price is registered*");
    }

    [Fact]
    public async Task A_retired_price_is_not_a_price_this_node_can_sell_at()
    {
        var services = BuildProvider(
            DeclaredOnRamp(),
            new[] { ActivePrice() with { Status = PriceScheduleStatuses.Retired } });

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*no current price is registered*");
    }

    [Fact]
    public async Task A_price_older_than_the_on_ramp_will_apply_refuses_to_boot()
    {
        // The settlement handler parks a payment priced by a declaration older than
        // PriceScheduleLimits.MaxDeclarationAge. A boot guard that accepted one would pass a node
        // whose every payment the settlement path is about to refuse.
        var stale = ActivePrice() with
        {
            DeclaredAt = Now - PriceScheduleLimits.MaxDeclarationAge - TimeSpan.FromDays(1),
        };

        var services = BuildProvider(DeclaredOnRamp(), new[] { stale });

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*no current price is registered*");
    }

    [Fact]
    public async Task A_price_dated_after_the_payment_it_would_price_refuses_to_boot()
    {
        var future = ActivePrice() with
        {
            DeclaredAt = Now + PriceScheduleLimits.FutureDeclarationTolerance + TimeSpan.FromMinutes(1),
        };

        var services = BuildProvider(DeclaredOnRamp(), new[] { future });

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*no current price is registered*");
    }

    [Fact]
    public async Task One_scope_holding_a_current_price_is_enough_to_boot()
    {
        // Whether one particular tenant registered its own price is a per-payment refusal the
        // settlement handler already makes. The boot question is whether ANY payment could ever
        // be converted, so a retired tenant price beside a live node price still boots.
        var services = BuildProvider(
            DeclaredOnRamp(),
            new[]
            {
                ActivePrice() with { ScopeKey = "deadbeef", Status = PriceScheduleStatuses.Retired },
                ActivePrice(),
            });

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        await act.Should().NotThrowAsync();
    }

    // ── Composition: the on-ramp itself has to be wired ──────────────────────

    [Fact]
    public async Task A_live_node_with_no_on_ramp_registered_refuses_to_boot()
    {
        var services = BuildProvider(
            DeclaredOnRamp(), new[] { ActivePrice() }, registerOnRampOptions: false);

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*on-ramp is not registered*");
    }

    [Fact]
    public async Task The_guard_reads_the_scoped_stores_through_a_scope()
    {
        // Both stores are registered Scoped by AddFiatOnRamp. Resolving them off the root provider
        // is what a validated container refuses, so this fails loudly if the guard ever stops
        // creating its own scope.
        var services = BuildProvider(
            DeclaredOnRamp(), new[] { ActivePrice() }, validateScopes: true);

        var act = () => FiatRailStartup.GuardLiveCapabilityAsync(services, LiveOptions(), Now);

        await act.Should().NotThrowAsync();
    }

    // ── The guard's own scope: not every node is a live money node ───────────

    [Fact]
    public async Task A_test_environment_node_boots_without_reading_either_store()
    {
        var reserve = new Mock<IPlatformReserve>();
        var schedules = new Mock<ITenantPriceScheduleStore>();
        var services = BuildProvider(
            DeclaredOnRamp(), new[] { ActivePrice() }, reserve: reserve, schedulesMock: schedules);

        var options = LiveOptions();
        options.Environment = RailEnvironmentNames.Test;

        await FiatRailStartup.GuardLiveCapabilityAsync(services, options, Now);

        // A dev node has neither a genesised platform unit nor a declared partition, and must
        // still boot against an empty database.
        reserve.Verify(
            store => store.GetLedgerAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        schedules.Verify(
            store => store.ListLatestPerScopeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_disabled_rail_boots_without_reading_either_store()
    {
        var reserve = new Mock<IPlatformReserve>();
        var schedules = new Mock<ITenantPriceScheduleStore>();
        var services = BuildProvider(
            DeclaredOnRamp(), new[] { ActivePrice() }, reserve: reserve, schedulesMock: schedules);

        var options = LiveOptions();
        options.Enabled = false;

        await FiatRailStartup.GuardLiveCapabilityAsync(services, options, Now);

        reserve.Verify(
            store => store.GetLedgerAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        schedules.Verify(
            store => store.ListLatestPerScopeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>A fully configured live rail; only Enabled and Environment reach the guard.</summary>
    private static FiatOptions LiveOptions() =>
        new() { Enabled = true, Environment = RailEnvironmentNames.Live };

    /// <summary>The partition map of a node an operator has actually prepared.</summary>
    private static Dictionary<string, ReservePartitionCounters> DeclaredOnRamp() =>
        new()
        {
            [PartitionName] = ReservePartitionCounters.Declaring(new BigInteger(1_000_000)),
        };

    /// <summary>The settled base price: 1 ARDA = $0.01 at Decimals = 2, so 100 units per cent.</summary>
    private static RegisteredPriceSchedule ActivePrice() => new(
        RecordId: "tenant_price_schedule:node_algorand_742000001_1",
        ScopeKey: PriceScheduleScope.Node,
        TenantId: null,
        ChainType: ChainType,
        AssetId: AssetId,
        CurrencyCode: PriceScheduleLimits.SupportedCurrencyCode,
        Version: 1,
        UnitsPerMinorUnit: 100m,
        Source: "operator",
        Note: null,
        Status: PriceScheduleStatuses.Active,
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

    private static PlatformReserveLedger LedgerRow(JsonElement partitions) => new()
    {
        Id = PlatformReserveLedger.RecordIdFor(ChainType, "Mainnet", AssetId),
        AssetId = AssetId,
        Chain = ChainType,
        Network = "Mainnet",
        ReserveAddress = ReserveAddress,
        UnitName = PlatformTokenGenesisDefaults.UnitName,
        Decimals = PlatformTokenGenesisDefaults.Decimals,
        TotalSupply = "2000000000",
        Partitions = partitions,
        Version = 1,
        CreatedAt = Now - TimeSpan.FromDays(30),
        UpdatedAt = Now - TimeSpan.FromDays(30),
    };

    /// <summary>
    /// A container shaped like the composed node: the on-ramp options as a singleton instance,
    /// the platform unit, the reserve and the price schedule all SCOPED, exactly as
    /// <c>AddFiatOnRamp</c> and <c>Program.cs</c> register them.
    /// </summary>
    private static ServiceProvider BuildProvider(
        Dictionary<string, ReservePartitionCounters> partitions,
        IReadOnlyList<RegisteredPriceSchedule> schedules,
        string configuredPartition = PartitionName,
        bool registerOnRampOptions = true,
        bool validateScopes = false,
        JsonElement? partitionsOverride = null,
        AZOAResult<PlatformReserveLedger>? ledgerResult = null,
        AZOAResult<PlatformTokenState>? tokenStateResult = null,
        Mock<IPlatformReserve>? reserve = null,
        Mock<ITenantPriceScheduleStore>? schedulesMock = null)
    {
        var genesis = new Mock<IPlatformTokenGenesis>();
        genesis
            .Setup(store => store.GetStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tokenStateResult
                ?? AZOAResult<PlatformTokenState>.Success(PlatformUnit()));

        var reserveMock = reserve ?? new Mock<IPlatformReserve>();
        reserveMock
            .Setup(store => store.GetLedgerAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ledgerResult
                ?? AZOAResult<PlatformReserveLedger>.Success(
                    LedgerRow(partitionsOverride ?? ReservePartitionCodec.Encode(partitions))));

        var priceMock = schedulesMock ?? new Mock<ITenantPriceScheduleStore>();
        priceMock
            .Setup(store => store.ListLatestPerScopeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>.Success(schedules));

        var services = new ServiceCollection();
        if (registerOnRampOptions)
        {
            services.AddSingleton(new OnRampOptions { ReservePartition = configuredPartition });
        }

        services.AddScoped(_ => genesis.Object);
        services.AddScoped(_ => reserveMock.Object);
        services.AddScoped(_ => priceMock.Object);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = validateScopes,
        });
    }
}
