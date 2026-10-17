// SPDX-License-Identifier: UNLICENSED
// OI-64 -- the D16 value stamp must not go dark the moment a second scope prices one asset.
//
// The stamp is handed a chain and an asset and no scope. Before this, ANY second scope holding a
// current price made PriceScheduleDeclaredRateStore return null, which every consumer records as
// "never priced" -- so one tenant registering a schedule for the platform unit silently unpriced
// every operation node-wide, on a LogWarning nobody was watching. These tests pin the precedence
// that resolves it, and pin the one case that stays unresolvable as an operator ALERT rather than
// a warning.

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Fiat;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AZOA.WebAPI.Tests.Fiat;

public class TenantPriceScheduleStoreTests
{
    private const string ChainType = "algorand";
    private const string AssetId = "1001";

    /// <summary>The settled base: 1 ARDA = $0.01 at Decimals = 2, so one cent buys 100 base units.</summary>
    private const decimal NodeRate = 100m;

    /// <summary>A tenant's own, deliberately different, price -- so a substitution would be visible.</summary>
    private const decimal TenantRate = 250m;

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly string TenantScope = new Guid("33333333333333333333333333333333").ToString("N");
    private static readonly string OtherTenantScope = new Guid("44444444444444444444444444444444").ToString("N");

    // -- The defect: a second tenant must not blank the platform stamp --------

    [Fact]
    public async Task A_second_tenants_schedule_does_not_blank_the_platform_stamp()
    {
        var logger = new RecordingLogger();
        var store = NewStore(
            logger,
            Schedule(PriceScheduleScope.Node, NodeRate, version: 3),
            Schedule(TenantScope, TenantRate, version: 1));

        var declared = await store.TryGetLatestAsync(ChainType, AssetId);

        declared.Should().NotBeNull(
            "a tenant registering its own price cannot unprice every operation on this node");
        declared!.MinorUnitsPerBaseUnit.Should().Be(0.01m, "the node-wide price is the one it resolves to");
        declared.Version.Should().Be("3");
        declared.Source.Should().Be(SourceFor(PriceScheduleScope.Node));
    }

    [Fact]
    public async Task The_tenants_own_price_is_never_substituted_for_the_platforms()
    {
        var store = NewStore(
            new RecordingLogger(),
            Schedule(PriceScheduleScope.Node, NodeRate, version: 3),
            Schedule(TenantScope, TenantRate, version: 7));

        var declared = await store.TryGetLatestAsync(ChainType, AssetId);

        // 1/250 -- the figure a substitution would produce.
        declared!.MinorUnitsPerBaseUnit.Should().NotBe(0.004m);
        declared.Source.Should().NotBe(SourceFor(TenantScope));
    }

    [Fact]
    public async Task A_single_tenant_scope_still_stamps_because_it_is_unambiguous()
    {
        var store = NewStore(new RecordingLogger(), Schedule(TenantScope, NodeRate, version: 2));

        var declared = await store.TryGetLatestAsync(ChainType, AssetId);

        declared.Should().NotBeNull();
        declared!.Version.Should().Be("2");
        declared.Source.Should().Be(SourceFor(TenantScope));
    }

    [Fact]
    public async Task A_retired_node_price_leaves_the_one_active_tenant_scope_to_stamp()
    {
        var store = NewStore(
            new RecordingLogger(),
            Schedule(PriceScheduleScope.Node, NodeRate, version: 4, status: PriceScheduleStatuses.Retired),
            Schedule(TenantScope, TenantRate, version: 1));

        var declared = await store.TryGetLatestAsync(ChainType, AssetId);

        declared.Should().NotBeNull("a retired node price is not a current price, so it does not compete");
        declared!.Source.Should().Be(SourceFor(TenantScope));
    }

    // -- The case that stays unresolvable, now as an alert --------------------

    [Fact]
    public async Task Two_tenant_scopes_with_no_node_price_stay_unpriced_and_raise_an_operator_alert()
    {
        var logger = new RecordingLogger();
        var store = NewStore(
            logger,
            Schedule(TenantScope, NodeRate, version: 1),
            Schedule(OtherTenantScope, TenantRate, version: 1));

        var declared = await store.TryGetLatestAsync(ChainType, AssetId);

        declared.Should().BeNull(
            "which of two tenants priced a given operation is not knowable from a chain and an asset");

        logger.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Error && entry.Message.Contains("MANUAL INTERVENTION REQUIRED"),
            "an asset nobody can price is an operator alert, not a warning nobody reads");
    }

    [Fact]
    public async Task An_unresolvable_asset_is_recorded_unpriced_rather_than_guessed_from_configuration()
    {
        var configured = new TenantDeclaredRate(ChainType, AssetId, "USD", 0.02m, Now.AddHours(-1), "configured");
        var store = NewStore(
            new RecordingLogger(),
            new AlwaysDeclares(configured),
            Schedule(TenantScope, NodeRate, version: 1),
            Schedule(OtherTenantScope, TenantRate, version: 1));

        // Reaching for ValueRates:Declared here would stamp a THIRD figure that priced nothing.
        (await store.TryGetLatestAsync(ChainType, AssetId)).Should().BeNull();
    }

    [Fact]
    public async Task The_resolved_scope_is_reported_so_the_substitution_is_never_silent()
    {
        var logger = new RecordingLogger();
        var store = NewStore(
            logger,
            Schedule(PriceScheduleScope.Node, NodeRate, version: 3),
            Schedule(TenantScope, TenantRate, version: 1));

        await store.TryGetLatestAsync(ChainType, AssetId);

        logger.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("node-wide price"),
            "an operator has to be able to see that two scopes disagreed and which one won");
    }

    // -- helpers -------------------------------------------------------------

    private static PriceScheduleDeclaredRateStore NewStore(
        RecordingLogger logger, params RegisteredPriceSchedule[] schedules) =>
        NewStore(logger, NeverDeclares.Instance, schedules);

    private static PriceScheduleDeclaredRateStore NewStore(
        RecordingLogger logger,
        ITenantDeclaredRateStore fallback,
        params RegisteredPriceSchedule[] schedules) =>
        new(new FakeScheduleStore(schedules), fallback, logger);

    /// <summary>A source label that names its scope, so a substituted price is identifiable.</summary>
    private static string SourceFor(string scopeKey) => $"treasury:{scopeKey}";

    private static RegisteredPriceSchedule Schedule(
        string scopeKey,
        decimal rate,
        long version,
        string status = PriceScheduleStatuses.Active) =>
        new(
            RecordId: $"tenant_price_schedule:{scopeKey}-{version}",
            ScopeKey: scopeKey,
            TenantId: null,
            ChainType: ChainType,
            AssetId: AssetId,
            CurrencyCode: "USD",
            Version: version,
            UnitsPerMinorUnit: rate,
            Source: SourceFor(scopeKey),
            Note: null,
            Status: status,
            DeclaredAt: Now.AddHours(-1),
            RegisteredAt: Now.AddHours(-1));

    /// <summary>Hands back exactly the current-per-scope rows it was built with.</summary>
    private sealed class FakeScheduleStore : ITenantPriceScheduleStore
    {
        private readonly IReadOnlyList<RegisteredPriceSchedule> _schedules;

        public FakeScheduleStore(params RegisteredPriceSchedule[] schedules) => _schedules = schedules;

        public Task<AZOAResult<RegisteredPriceSchedule>> RegisterAsync(
            PriceScheduleRegistration registration,
            string scopeKey,
            Guid? tenantId,
            Guid registeredByUserId,
            CancellationToken ct = default)
            => throw new NotSupportedException("The fake store is read-only.");

        public Task<AZOAResult<RegisteredPriceSchedule?>> GetLatestAsync(
            string scopeKey, string chainType, string assetId, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<RegisteredPriceSchedule?>
            {
                Result = _schedules.FirstOrDefault(
                    schedule => string.Equals(schedule.ScopeKey, scopeKey, StringComparison.Ordinal)),
            });

        public Task<AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>> ListVersionsAsync(
            string scopeKey, string chainType, string assetId, int limit, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>.Success(
                _schedules
                    .Where(schedule => string.Equals(schedule.ScopeKey, scopeKey, StringComparison.Ordinal))
                    .ToList()));

        public Task<AZOAResult<RegisteredPriceSchedule?>> GetVersionAsync(string recordId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>> ListLatestPerScopeAsync(
            string chainType, string assetId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>.Success(_schedules.ToList()));
    }

    /// <summary>The honest "nothing is declared" configuration store.</summary>
    private sealed class NeverDeclares : ITenantDeclaredRateStore
    {
        public static NeverDeclares Instance { get; } = new();

        public ValueTask<TenantDeclaredRate?> TryGetLatestAsync(
            string chainType, string assetId, CancellationToken ct = default)
            => ValueTask.FromResult<TenantDeclaredRate?>(null);
    }

    /// <summary>A configuration store that always quotes, so reaching it is observable.</summary>
    private sealed class AlwaysDeclares(TenantDeclaredRate rate) : ITenantDeclaredRateStore
    {
        public ValueTask<TenantDeclaredRate?> TryGetLatestAsync(
            string chainType, string assetId, CancellationToken ct = default)
            => ValueTask.FromResult<TenantDeclaredRate?>(rate);
    }

    /// <summary>Captures level and formatted message so "alert, not warning" is assertable.</summary>
    private sealed class RecordingLogger : ILogger<PriceScheduleDeclaredRateStore>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
