using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealNftProtocolCostQuoteTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private SurrealNftProtocolCostQuoteStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "nft_protocol_cost_quote");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        _store = new(new DefaultSurrealExecutor(_connection));
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task ReplayRetainsOriginalExpiryAndNeverUpdatesAnOffer()
    {
        var quote = Quote();
        (await _store.ClaimAsync(quote)).Should().Be(quote);
        (await _store.ClaimAsync(quote)).Should().Be(quote);
        (await _store.ClaimAsync(quote with { ExpiresAt = quote.ExpiresAt.AddMinutes(1) })).Should().BeNull();
        (await _store.GetAsync(quote.QuoteId, quote.ActorUserId, quote.ActingTenantId)).Should().Be(quote);
        (await _store.GetAsync(quote.QuoteId, Guid.NewGuid(), quote.ActingTenantId)).Should().BeNull();
        (await _store.GetAsync(quote.QuoteId, quote.ActorUserId, Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentConflictingOffersHaveOneImmutableWinner()
    {
        var quote = Quote();
        var other = quote with { DraftHash = new string('b', 64) };
        var results = await Task.WhenAll(_store.ClaimAsync(quote), _store.ClaimAsync(other));
        results.Where(result => result is not null).Should().ContainSingle();
        var winner = results.Single(result => result is not null);
        (await _store.GetAsync(quote.QuoteId, quote.ActorUserId, quote.ActingTenantId)).Should().Be(winner);
    }

    private static NftProtocolCostQuote Quote() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.Empty,
        Guid.NewGuid(), "payer", "Solana", ChainNetwork.Devnet, "genesis", "lifecycle", new string('a', 64),
        "Create", 0, "NonFungible", "{\"revision\":1}", new("5000", "10000000", "10000000"),
        DateTimeOffset.Parse("2025-01-01T00:00:00Z"), DateTimeOffset.Parse("2025-01-01T00:05:00Z"));
}
