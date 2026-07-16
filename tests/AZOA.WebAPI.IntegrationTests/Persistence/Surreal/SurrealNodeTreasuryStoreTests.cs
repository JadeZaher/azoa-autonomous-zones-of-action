using AZOA.WebAPI.Core;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealNodeTreasuryStoreTests : IAsyncLifetime
{
    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealNodeTreasuryStore _store = null!;
    private HttpSurrealConnection _connection = null!;
    private bool _surrealAvailable;

    public async Task InitializeAsync()
    {
        _surrealAvailable = await ProbeSurrealAsync();
        if (!_surrealAvailable)
            return;

        var options = new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _testNamespace,
            Database = "test",
            User = SurrealTestDefaults.User,
            Password = SurrealTestDefaults.Password,
        };
        var http = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        _connection = new HttpSurrealConnection(http, options);
        _store = new SurrealNodeTreasuryStore(new DefaultSurrealExecutor(_connection));

        await SurrealTestSchema.BootstrapAsync(
            _testNamespace,
            NodeTreasuryDestination.SchemaNameConst,
            NodeTreasuryAudit.SchemaNameConst);
    }

    public async Task DisposeAsync()
    {
        if (!_surrealAvailable || _connection is null)
            return;

        try
        {
            await SurrealTestSchema.DropAsync(_testNamespace);
        }
        catch
        {
            // Best-effort test isolation cleanup.
        }
        finally
        {
            _connection.Dispose();
        }
    }

    [SkippableFact]
    public async Task UpdateWithAudit_ThenRead_RoundTripsAtomically()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var actorLink = ActorLink();
        var now = DateTimeOffset.UtcNow;
        var destination = Destination(
            "Simulated",
            ChainNetwork.Devnet,
            "sim:simulated:ALGO_TREASURY",
            1,
            actorLink,
            now);
        var audit = Audit(destination, actorLink, previousVersion: 0, occurredAt: now);

        var saved = await _store.UpdateDestinationWithAuditAsync(destination, audit, expectedVersion: null);

        saved.IsError.Should().BeFalse(saved.Message);
        saved.Result.Should().NotBeNull();
        saved.Result!.Id.Should().Be(destination.Id);
        saved.Result.Address.Should().Be("sim:simulated:ALGO_TREASURY");
        saved.Result.Version.Should().Be(1);

        var loaded = await _store.GetDestinationAsync("Simulated", ChainNetwork.Devnet);
        loaded.IsError.Should().BeFalse(loaded.Message);
        loaded.Result.Should().NotBeNull();
        loaded.Result!.Address.Should().Be("sim:simulated:ALGO_TREASURY");
        loaded.Result.UpdatedByAvatarId.Should().Be(actorLink);

        var audits = await _store.ListAuditAsync(10);
        audits.IsError.Should().BeFalse(audits.Message);
        audits.Result.Should().ContainSingle();
        var persistedAudit = audits.Result!.Single();
        persistedAudit.Id.Should().Be(audit.Id);
        persistedAudit.Chain.Should().Be("Simulated");
        persistedAudit.Network.Should().Be(nameof(ChainNetwork.Devnet));
        persistedAudit.PreviousVersion.Should().Be(0);
        persistedAudit.NewVersion.Should().Be(1);
        persistedAudit.DestinationJson.Should().Contain("sim:simulated:ALGO_TREASURY");
    }

    [SkippableFact]
    public async Task Destinations_AreScopedByCanonicalChainAndNetworkKey()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var actorLink = ActorLink();
        var now = DateTimeOffset.UtcNow;
        var devnet = Destination("Algorand", ChainNetwork.Devnet, "ALGO_DEV", 1, actorLink, now);
        var testnet = Destination("Algorand", ChainNetwork.Testnet, "ALGO_TEST", 1, actorLink, now);
        var solana = Destination("Solana", ChainNetwork.Devnet, "SOL_DEV", 1, actorLink, now);

        var writes = await Task.WhenAll(
            _store.UpdateDestinationWithAuditAsync(devnet, Audit(devnet, actorLink, 0, now), null),
            _store.UpdateDestinationWithAuditAsync(testnet, Audit(testnet, actorLink, 0, now), null),
            _store.UpdateDestinationWithAuditAsync(solana, Audit(solana, actorLink, 0, now), null));

        writes.Should().OnlyContain(result => !result.IsError);
        var loadedDevnet = await _store.GetDestinationAsync("algorand", ChainNetwork.Devnet);
        var loadedTestnet = await _store.GetDestinationAsync("ALGORAND", ChainNetwork.Testnet);
        var loadedSolana = await _store.GetDestinationAsync("Solana", ChainNetwork.Devnet);
        var miss = await _store.GetDestinationAsync("Solana", ChainNetwork.Mainnet);

        loadedDevnet.Result!.Address.Should().Be("ALGO_DEV");
        loadedTestnet.Result!.Address.Should().Be("ALGO_TEST");
        loadedSolana.Result!.Address.Should().Be("SOL_DEV");
        miss.IsError.Should().BeFalse(miss.Message);
        miss.Result.Should().BeNull();

        var audits = await _store.ListAuditAsync(10);
        audits.Result.Should().HaveCount(3);
        audits.Result.Should().ContainSingle(row =>
            row.Chain == "Algorand" && row.Network == nameof(ChainNetwork.Testnet));
        audits.Result.Should().ContainSingle(row =>
            row.Chain == "Solana" && row.Network == nameof(ChainNetwork.Devnet));
    }

    [SkippableFact]
    public async Task ConcurrentSameVersionUpdate_AllowsExactlyOneWriterAndAudit()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var actorLink = ActorLink();
        var createdAt = DateTimeOffset.UtcNow;
        var initial = Destination("Algorand", ChainNetwork.Devnet, "INITIAL", 1, actorLink, createdAt);
        var seeded = await _store.UpdateDestinationWithAuditAsync(
            initial,
            Audit(initial, actorLink, previousVersion: 0, occurredAt: createdAt),
            expectedVersion: null);
        seeded.IsError.Should().BeFalse(seeded.Message);

        var updatedAt = createdAt.AddSeconds(1);
        var first = Destination("Algorand", ChainNetwork.Devnet, "TREASURY_A", 2, actorLink, createdAt, updatedAt);
        var second = Destination("Algorand", ChainNetwork.Devnet, "TREASURY_B", 2, actorLink, createdAt, updatedAt);

        var results = await Task.WhenAll(
            _store.UpdateDestinationWithAuditAsync(
                first,
                Audit(
                    first,
                    actorLink,
                    previousVersion: 1,
                    occurredAt: updatedAt,
                    previousAddress: "INITIAL"),
                expectedVersion: 1),
            _store.UpdateDestinationWithAuditAsync(
                second,
                Audit(
                    second,
                    actorLink,
                    previousVersion: 1,
                    occurredAt: updatedAt,
                    previousAddress: "INITIAL"),
                expectedVersion: 1));

        results.Count(result => !result.IsError).Should().Be(1,
            "the compare-and-set transaction must elect one writer");
        results.Count(result => result.IsError).Should().Be(1);
        results.Single(result => result.IsError).Message.Should().Contain("version conflict");

        var loaded = await _store.GetDestinationAsync("Algorand", ChainNetwork.Devnet);
        loaded.Result!.Version.Should().Be(2);
        loaded.Result.Address.Should().BeOneOf("TREASURY_A", "TREASURY_B");

        var audits = await _store.ListAuditAsync(10);
        audits.Result.Should().HaveCount(2,
            "the losing transaction must not append its audit record");
        audits.Result.Should().ContainSingle(row => row.NewVersion == 2);
        audits.Result!.Single(row => row.NewVersion == 2).DestinationJson
            .Should().Contain(loaded.Result.Address);
    }

    private static NodeTreasuryDestination Destination(
        string chain,
        ChainNetwork network,
        string address,
        long version,
        string actorLink,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt = null)
        => new()
        {
            Id = NodeTreasuryDestination.RecordIdFor(chain, network.ToString()),
            Chain = chain,
            Network = network.ToString(),
            Address = address,
            Version = version,
            UpdatedByAvatarId = actorLink,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt ?? createdAt,
        };

    private static NodeTreasuryAudit Audit(
        NodeTreasuryDestination destination,
        string actorLink,
        long previousVersion,
        DateTimeOffset occurredAt,
        string? previousAddress = null)
        => new()
        {
            Id = SurrealId.ToSurrealId(Guid.NewGuid()),
            Action = "DestinationUpdated",
            ActorAvatarId = actorLink,
            Chain = destination.Chain,
            Network = destination.Network,
            PreviousVersion = previousVersion,
            NewVersion = destination.Version,
            PreviousDestinationJson = previousAddress is null
                ? null
                : $"{{\"address\":\"{previousAddress}\"}}",
            DestinationJson = $"{{\"address\":\"{destination.Address}\"}}",
            Detail = "Integration test update.",
            OccurredAt = occurredAt,
        };

    private static string ActorLink()
        => SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(Guid.NewGuid()))!;

    private static async Task<bool> ProbeSurrealAsync()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await probe.GetAsync($"{SurrealTestDefaults.Endpoint}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
