// SPDX-License-Identifier: UNLICENSED
// W5.1 — the durable declared-rate store: what it records, what it refuses to overwrite, and
// what it hands to the fallback it composed.

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public class HolonTenantDeclaredRateStoreTests
{
    private const string Chain = "Algorand";
    private const string ProjectAsset = "2002";

    private static readonly DateTimeOffset Noon = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_recorded_rate_is_read_back_with_every_field_the_consumer_declared()
    {
        var holons = new InMemoryHolonStore();
        var store = new HolonTenantDeclaredRateStore(holons);

        await store.RecordAsync(Rate());
        var read = await store.TryGetLatestAsync(Chain, ProjectAsset);

        read.Should().NotBeNull();
        read!.MinorUnitsPerBaseUnit.Should().Be(0.05m);
        read.Source.Should().Be("ardanova:treasury-rate");
        read.Version.Should().Be("1");
        read.DeclaredAt.Should().Be(Noon);
        read.CurrencyCode.Should().Be("USD");
    }

    [Fact]
    public async Task An_older_declaration_never_displaces_a_newer_one()
    {
        var holons = new InMemoryHolonStore();
        var store = new HolonTenantDeclaredRateStore(holons);

        await store.RecordAsync(Rate(minorUnits: 0.05m, declaredAt: Noon));
        await store.RecordAsync(Rate(minorUnits: 0.99m, declaredAt: Noon.AddHours(-1)));

        var read = await store.TryGetLatestAsync(Chain, ProjectAsset);
        read!.MinorUnitsPerBaseUnit.Should().Be(
            0.05m, "an out-of-order retry must not re-price what later exchanges already settled at");
    }

    [Fact]
    public async Task A_newer_declaration_replaces_the_one_before_it()
    {
        var holons = new InMemoryHolonStore();
        var store = new HolonTenantDeclaredRateStore(holons);

        await store.RecordAsync(Rate(minorUnits: 0.05m, declaredAt: Noon));
        await store.RecordAsync(Rate(minorUnits: 0.07m, declaredAt: Noon.AddHours(1)));

        var read = await store.TryGetLatestAsync(Chain, ProjectAsset);
        read!.MinorUnitsPerBaseUnit.Should().Be(0.07m);
        holons.Count.Should().Be(1, "one asset on one rail has one current price, not a growing pile");
    }

    [Fact]
    public async Task An_asset_this_store_has_never_priced_falls_through_to_the_composed_store()
    {
        var fallback = new StubDeclaredRateStore(Rate(assetId: "9999", minorUnits: 0.42m));
        var store = new HolonTenantDeclaredRateStore(new InMemoryHolonStore(), fallback);

        var read = await store.TryGetLatestAsync(Chain, "9999");

        read!.MinorUnitsPerBaseUnit.Should().Be(
            0.42m, "dropping the displaced store would stop pricing every asset the exchange never saw");
    }

    [Fact]
    public async Task A_recorded_rate_wins_over_the_composed_store_for_the_same_asset()
    {
        var fallback = new StubDeclaredRateStore(Rate(minorUnits: 0.42m));
        var store = new HolonTenantDeclaredRateStore(new InMemoryHolonStore(), fallback);

        await store.RecordAsync(Rate(minorUnits: 0.05m));
        var read = await store.TryGetLatestAsync(Chain, ProjectAsset);

        read!.MinorUnitsPerBaseUnit.Should().Be(0.05m);
    }

    [Fact]
    public async Task Two_assets_never_share_a_price()
    {
        var store = new HolonTenantDeclaredRateStore(new InMemoryHolonStore());

        await store.RecordAsync(Rate(assetId: "2002", minorUnits: 0.05m));
        await store.RecordAsync(Rate(assetId: "3003", minorUnits: 0.90m));

        (await store.TryGetLatestAsync(Chain, "2002"))!.MinorUnitsPerBaseUnit.Should().Be(0.05m);
        (await store.TryGetLatestAsync(Chain, "3003"))!.MinorUnitsPerBaseUnit.Should().Be(0.90m);
    }

    [Fact]
    public async Task Nothing_is_priced_when_nothing_was_declared()
    {
        var store = new HolonTenantDeclaredRateStore(new InMemoryHolonStore());

        (await store.TryGetLatestAsync(Chain, ProjectAsset)).Should().BeNull(
            "an absent rate must read back as never priced, never as zero");
    }

    private static TenantDeclaredRate Rate(
        string assetId = ProjectAsset,
        decimal minorUnits = 0.05m,
        DateTimeOffset? declaredAt = null)
        => new(Chain, assetId, "USD", minorUnits, declaredAt ?? Noon, "ardanova:treasury-rate", "1");

    private sealed class StubDeclaredRateStore : ITenantDeclaredRateStore
    {
        private readonly TenantDeclaredRate _rate;

        public StubDeclaredRateStore(TenantDeclaredRate rate) => _rate = rate;

        public ValueTask<TenantDeclaredRate?> TryGetLatestAsync(
            string chainType, string assetId, CancellationToken ct = default)
            => ValueTask.FromResult<TenantDeclaredRate?>(
                string.Equals(_rate.AssetId, assetId, StringComparison.Ordinal) ? _rate : null);
    }

    private sealed class InMemoryHolonStore : IHolonStore
    {
        private readonly Dictionary<Guid, IHolon> _byId = new();

        public int Count => _byId.Count;

        public Task<AZOAResult<IHolon>> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_byId.TryGetValue(id, out var found)
                ? AZOAResult<IHolon>.Success(found)
                : AZOAResult<IHolon>.FailureWithCode("Holon not found.", AzoaErrorCodes.NotFound));

        public Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(
            HolonQueryRequest? query = null, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IEnumerable<IHolon>>.Success(_byId.Values.ToList()));

        public Task<AZOAResult<IHolon>> UpsertAsync(IHolon holon, CancellationToken ct = default)
        {
            _byId[holon.Id] = holon;
            return Task.FromResult(AZOAResult<IHolon>.Success(holon));
        }

        public Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<bool>.Success(_byId.Remove(id)));

        public Task<AZOAResult<bool>> TryReserveNftTransferAsync(
            Guid holonId, Guid sourceUserId, Guid targetUserId, string settlementKey,
            CancellationToken ct = default)
            => Task.FromResult(AZOAResult<bool>.Success(false));

        public Task<AZOAResult<bool>> FinalizeReservedNftTransferAsync(
            Guid holonId, Guid sourceUserId, Guid targetUserId, string settlementKey,
            CancellationToken ct = default)
            => Task.FromResult(AZOAResult<bool>.Success(false));

        public Task<AZOAResult<bool>> ReleaseNftTransferReservationAsync(
            Guid holonId, Guid sourceUserId, string settlementKey, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<bool>.Success(false));
    }
}
