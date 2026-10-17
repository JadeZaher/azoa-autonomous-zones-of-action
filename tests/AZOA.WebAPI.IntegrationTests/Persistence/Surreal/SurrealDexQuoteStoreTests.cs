using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealDexQuoteStoreTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealDexQuoteStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "dex_quote");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new(_executor);
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task ConcurrentIdenticalQuotesAndReconstructedStoreKeepOriginalImmutableTerms()
    {
        var candidate = Candidate();
        var results = await Task.WhenAll(_store.ClaimAsync(candidate), _store.ClaimAsync(Clone(candidate)));
        results.Should().OnlyContain(row => row != null && row.QuoteHash == candidate.QuoteHash);
        var recovered = await new SurrealDexQuoteStore(_executor).GetAsync(candidate.Id);
        recovered!.QuoteJson.Should().Be(candidate.QuoteJson);
        recovered.QuoteJson.Should().Contain("18446744073709551615");
        recovered.ConsumedByOperationId.Should().BeNull();
    }

    [Fact]
    public async Task SameQuoteIdentityCannotChangeEvenWithSelfConsistentNewHash()
    {
        var candidate = Candidate();
        await _store.ClaimAsync(candidate);
        var changed = Clone(candidate);
        var terms = JsonSerializer.Deserialize<DexQuoteTerms>(changed.QuoteJson)! with { MinimumOutput = "800" };
        changed.QuoteJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(terms));
        changed.QuoteHash = CanonicalJson.HashCanonical(changed.QuoteJson);
        (await _store.ClaimAsync(changed)).Should().BeNull();
        (await _store.GetAsync(candidate.Id))!.QuoteJson.Should().Be(candidate.QuoteJson);
    }

    [Fact]
    public async Task ReplayAfterConsumptionCannotEraseWinningOperation()
    {
        var candidate = Candidate();
        await _store.ClaimAsync(candidate);
        var operation = Guid.NewGuid().ToString("N");
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<DexQuoteRecord>(candidate.Id).Where(seed => true)
            .Set(row => row.ConsumedByOperationId, operation).Build())).EnsureAllOk();
        (await _store.ClaimAsync(candidate))!.ConsumedByOperationId.Should().Be(operation);
        var forged = Clone(candidate);
        forged.ConsumedByOperationId = operation;
        (await _store.ClaimAsync(forged)).Should().BeNull();
    }

    [Fact]
    public async Task PrerequisiteDestinationAndRetentionArePartOfImmutableConsent()
    {
        var candidate = Candidate();
        await _store.ClaimAsync(candidate);
        var terms = JsonSerializer.Deserialize<DexQuoteTerms>(candidate.QuoteJson)!;
        var changed = Clone(candidate);
        changed.QuoteJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(terms with
        {
            Prerequisites = [new("OptIn", "2", "100000", "fixture-owner", "different-destination", false)],
        }));
        changed.QuoteHash = CanonicalJson.HashCanonical(changed.QuoteJson);
        (await _store.ClaimAsync(changed)).Should().BeNull();
        var saved = JsonSerializer.Deserialize<DexQuoteTerms>((await _store.GetAsync(candidate.Id))!.QuoteJson)!;
        saved.Prerequisites.Should().BeEquivalentTo(terms.Prerequisites);
    }

    [Fact]
    public async Task EmptyTenantIsNotTheDirectScope()
    {
        var candidate = Candidate();
        var terms = JsonSerializer.Deserialize<DexQuoteTerms>(candidate.QuoteJson)! with { TenantId = Guid.Empty };
        candidate.TenantId = Guid.Empty.ToString("N");
        candidate.QuoteJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(terms));
        candidate.QuoteHash = CanonicalJson.HashCanonical(candidate.QuoteJson);
        (await _store.ClaimAsync(candidate)).Should().BeNull();
        (await _store.GetAsync(candidate.Id)).Should().BeNull();
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("tenant")]
    [InlineData("hash")]
    [InlineData("expiry")]
    [InlineData("noncanonical")]
    public async Task CorruptAuthorityOrContentDoesNotCreateAQuote(string change)
    {
        var candidate = Candidate();
        switch (change)
        {
            case "actor": candidate.ActorUserId = Guid.NewGuid().ToString("N"); break;
            case "tenant": candidate.TenantId = Guid.NewGuid().ToString("N"); break;
            case "hash": candidate.QuoteHash = new string('0', 64); break;
            case "expiry": candidate.ExpiresAt = candidate.ExpiresAt.AddSeconds(1); break;
            default: candidate.QuoteJson = " " + candidate.QuoteJson; break;
        }
        (await _store.ClaimAsync(candidate)).Should().BeNull();
        (await _store.GetAsync(candidate.Id)).Should().BeNull();
    }

    private static DexQuoteRecord Clone(DexQuoteRecord candidate)
        => JsonSerializer.Deserialize<DexQuoteRecord>(JsonSerializer.Serialize(candidate))!;

    internal static DexQuoteRecord Candidate()
    {
        var actor = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var participant = new DexParticipant(actor, Guid.NewGuid(), "fixture-owner");
        var terms = new DexQuoteTerms(1, actor, null, participant, participant, participant.Address,
            new("Algorand", "Devnet", "fixture-genesis"), new("18446744073709551615", "IN", 6, false),
            new("2", "OUT", 6, false), "1000", "1000", "990", "900", 100,
            new("input-base-units-v1", 1, "0", 0, "0", ""), new("3000", "100000", "1000"),
            "Tinyman", "2", JsonSerializer.SerializeToElement(new { Pool = "fixture-pool" }), now.AddMinutes(2),
            [new("OptIn", "2", "100000", "fixture-owner", "fixture-owner", true)]);
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(terms));
        return new DexQuoteRecord
        {
            Id = Guid.NewGuid().ToString("N"), ActorUserId = actor.ToString("N"), TenantId = "",
            QuoteJson = json, QuoteHash = CanonicalJson.HashCanonical(json), ExpiresAt = terms.ExpiresAt, CreatedAt = now,
        };
    }
}
