using System.Text.Json;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Interleaving;

/// <summary>Redelivered settlements and concurrent cash-outs race on one on-ramp lot; see AGENTS.md §On-ramp lots.</summary>
public sealed class OnRampLotInterleavingTests : IAsyncLifetime
{
    private const int Racers = 4;
    private readonly Guid _subject = Guid.NewGuid();
    private InterleavingDatabase _database = null!;
    private SurrealOnRampLotLedger _ledger = null!;

    public async Task InitializeAsync()
    {
        _database = await InterleavingDatabase.CreateAsync("on_ramp_lot");
        _ledger = new SurrealOnRampLotLedger(_database.Executor);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task ConcurrentClaimsForOneFundingIntentCreateOneLotAndEveryLoserGetsTheWinnersRow()
    {
        var intent = Guid.NewGuid().ToString("N");
        var candidates = Enumerable.Range(1, Racers).Select(index => Lot(intent, (index * 100).ToString())).ToArray();

        var claims = await Task.WhenAll(candidates.Select(candidate => _ledger.ClaimAsync(candidate))).WaitAsync(TimeSpan.FromSeconds(15));

        claims.Should().OnlyContain(claim => !claim.IsError, "a duplicate-key loser resolves to the winner, not an error");
        claims.Where(claim => claim.Result!.Created).Should().ContainSingle("one funding intent settles into at most one lot");
        var winner = claims.Single(claim => claim.Result!.Created).Result!.Row;
        claims.Should().OnlyContain(claim => claim.Result!.Row.QuantityBaseUnits == winner.QuantityBaseUnits,
            "a loser's candidate quantity is never persisted or reported");
        var rows = await _database.Executor.QueryAsync<OnRampLot>(SurrealQuery<OnRampLot>.From());
        rows.Should().ContainSingle().Which.QuantityBaseUnits.Should().Be(winner.QuantityBaseUnits);
        (await _ledger.GetByIntentAsync(intent)).Result!.Version.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentCashOutsDrawingOneLotVersionConsumeItOnce()
    {
        var intent = Guid.NewGuid().ToString("N");
        var issued = Lot(intent, "1000");
        issued.Status = OnRampLotStatuses.Issued;
        issued.IssuedAt = DateTimeOffset.UtcNow.AddDays(-30);
        var lot = (await _ledger.ClaimAsync(issued)).Result!.Row;
        var draws = Enumerable.Range(0, Racers).Select(index => Draw(lot, (600 + index).ToString())).ToArray();

        var results = await Task.WhenAll(draws.Select(draw => _ledger.UpdateIfVersionAsync(draw, lot.Version)))
            .WaitAsync(TimeSpan.FromSeconds(15));

        results.Where(result => !result.IsError).Should().ContainSingle("the version is the compare-and-swap anchor");
        var current = (await _ledger.GetByIntentAsync(intent)).Result!;
        var winner = draws[Array.FindIndex(results, result => !result.IsError)];
        current.ConsumedBaseUnits.Should().Be(winner.ConsumedBaseUnits, "the loser re-reads the winner's watermark; units are never drawn twice");
        current.Version.Should().Be(lot.Version + 1);
    }

    private OnRampLot Lot(string intent, string quantity) => new()
    {
        Id = OnRampLot.RecordIdFor(intent), IntentId = SurrealLink.ToLink(HostedSession.SchemaNameConst, intent)!,
        SubjectUserId = SurrealLink.ToLink(User.SchemaNameConst, _subject.ToString("N"))!, ChainType = "Algorand", AssetId = "42",
        QuantityBaseUnits = quantity, GrossQuantityBaseUnits = quantity, FiatMinorUnits = 1000, CurrencyCode = "USD",
        Status = OnRampLotStatuses.Pending, AttemptCount = 1, Environment = "test", ChainNetwork = "Devnet",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static OnRampLot Draw(OnRampLot lot, string consumed)
    {
        var next = JsonSerializer.Deserialize<OnRampLot>(JsonSerializer.Serialize(lot))!;
        next.ConsumedBaseUnits = consumed;
        next.Version = lot.Version + 1;
        next.UpdatedAt = DateTimeOffset.UtcNow;
        return next;
    }
}
