using FluentAssertions;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Connection;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.IntegrationTests.Builders;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Providers.Stores.Surreal;

namespace OASIS.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealWalletStore"/>.
///
/// Each test creates an isolated SurrealDB namespace (test_{guid}) against
/// the test container on port 8442. Tests skip gracefully via <see cref="SkippableFact"/>
/// and <see cref="Skip.IfNot"/> when the SurrealDB container is unavailable, so the
/// test runner reports them as Skipped instead of Passed.
///
/// Minimum coverage required:
///   1. Upsert → GetById round-trip
///   2. GetByAvatar filters correctly
///   3. Delete removes the row
///   4. Upsert (update path) overwrites existing record
///   5. Delete of non-existent id returns IsError=true
///   6. GetById of non-existent id returns IsError=true
/// </summary>
public sealed class SurrealWalletStoreTests : IAsyncLifetime
{
    // ── Connection config ─────────────────────────────────────────────────────

    // Connection config sourced from SurrealTestDefaults (points at local instance).

    // ── Per-instance state ────────────────────────────────────────────────────

    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealWalletStore _store = null!;
    private HttpSurrealConnection _connection = null!;
    private bool _surrealAvailable;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        _surrealAvailable = await ProbeSurrealAsync();
        if (!_surrealAvailable) return;

        var options = new SurrealConnectionOptions
        {
            Endpoint  = SurrealTestDefaults.Endpoint,
            Namespace = _testNamespace,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password
        };

        var http = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        _connection = new HttpSurrealConnection(http, options);
        var executor = new DefaultSurrealExecutor(_connection);
        _store = new SurrealWalletStore(executor);

        await BootstrapSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_surrealAvailable || _connection is null) return;
        try { await DropNamespaceAsync(); }
        catch { /* best-effort */ }
        finally { _connection.Dispose(); }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>Test 1: Upsert creates a wallet; GetById retrieves it with matching fields.</summary>
    [SkippableFact]
    public async Task Upsert_ThenGetById_RoundTrips()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var wallet = new WalletBuilder()
            .ForAvatar(Guid.NewGuid())
            .OnChain("Algorand")
            .WithAddress($"algo_{Guid.NewGuid():N}")
            .WithLabel("Test Wallet")
            .Build();
        wallet.WalletType = WalletType.Platform;

        var upsertResult = await _store.UpsertAsync(wallet);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");
        upsertResult.Result.Should().NotBeNull();

        var getResult = await _store.GetByIdAsync(wallet.Id);

        getResult.IsError.Should().BeFalse();
        getResult.Message.Should().Be("Success");
        getResult.Result.Should().NotBeNull();
        getResult.Result!.Id.Should().Be(wallet.Id);
        getResult.Result.AvatarId.Should().Be(wallet.AvatarId);
        getResult.Result.ChainType.Should().Be("Algorand");
        getResult.Result.Label.Should().Be("Test Wallet");
        getResult.Result.WalletType.Should().Be(WalletType.Platform);
    }

    /// <summary>Test 2: GetById for a non-existent id returns IsError=true.</summary>
    [SkippableFact]
    public async Task GetById_NotFound_ReturnsError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.GetByIdAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("Wallet not found.");
        result.Result.Should().BeNull();
    }

    /// <summary>Test 3: GetByAvatar returns only wallets owned by that avatar.</summary>
    [SkippableFact]
    public async Task GetByAvatar_FiltersCorrectly()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var targetAvatar = Guid.NewGuid();
        var otherAvatar  = Guid.NewGuid();

        var w1 = new WalletBuilder().ForAvatar(targetAvatar).OnChain("Solana")
                                   .WithAddress($"sol_{Guid.NewGuid():N}").Build();
        var w2 = new WalletBuilder().ForAvatar(targetAvatar).OnChain("Ethereum")
                                   .WithAddress($"eth_{Guid.NewGuid():N}").Build();
        var w3 = new WalletBuilder().ForAvatar(otherAvatar).OnChain("Algorand")
                                   .WithAddress($"algo_{Guid.NewGuid():N}").Build();

        await _store.UpsertAsync(w1);
        await _store.UpsertAsync(w2);
        await _store.UpsertAsync(w3);

        var result = await _store.GetByAvatarAsync(targetAvatar);

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        var list = result.Result!.ToList();
        list.Should().HaveCount(2);
        list.All(w => w.AvatarId == targetAvatar).Should().BeTrue();
    }

    /// <summary>Test 4: Delete removes the wallet; subsequent GetById returns not-found.</summary>
    [SkippableFact]
    public async Task Delete_RemovesRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var wallet = new WalletBuilder()
            .ForAvatar(Guid.NewGuid())
            .WithAddress($"del_{Guid.NewGuid():N}")
            .Build();

        await _store.UpsertAsync(wallet);

        var deleteResult = await _store.DeleteAsync(wallet.Id);

        deleteResult.IsError.Should().BeFalse();
        deleteResult.Result.Should().BeTrue();
        deleteResult.Message.Should().Be("Deleted.");

        var getResult = await _store.GetByIdAsync(wallet.Id);
        getResult.IsError.Should().BeTrue();
        getResult.Message.Should().Be("Wallet not found.");
    }

    /// <summary>Test 5: Delete of a non-existent id returns IsError=true, Result=false.</summary>
    [SkippableFact]
    public async Task Delete_NotFound_ReturnsError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.DeleteAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().Be("Wallet not found.");
    }

    /// <summary>Test 6: Upsert (update path) overwrites the existing record.</summary>
    [SkippableFact]
    public async Task Upsert_UpdatePath_OverwritesExistingRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var id      = Guid.NewGuid();
        var avatarId = Guid.NewGuid();

        var original = new Wallet
        {
            Id        = id,
            AvatarId  = avatarId,
            ChainType = "Solana",
            Address   = $"sol_{Guid.NewGuid():N}",
            Label     = "Original Label",
            WalletType = WalletType.External
        };
        await _store.UpsertAsync(original);

        // Overwrite with updated label and wallet type.
        var updated = new Wallet
        {
            Id         = id,
            AvatarId   = avatarId,
            ChainType  = "Solana",
            Address    = original.Address,
            Label      = "Updated Label",
            WalletType = WalletType.Platform
        };
        var upsertResult = await _store.UpsertAsync(updated);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");

        var getResult = await _store.GetByIdAsync(id);
        getResult.IsError.Should().BeFalse();
        getResult.Result!.Label.Should().Be("Updated Label");
        getResult.Result.WalletType.Should().Be(WalletType.Platform);
    }

    /// <summary>Test 7: GetAll returns all wallets inserted in this namespace.</summary>
    [SkippableFact]
    public async Task GetAll_ReturnsAllInsertedWallets()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var avatarId = Guid.NewGuid();
        var w1 = new WalletBuilder().ForAvatar(avatarId).WithAddress($"a_{Guid.NewGuid():N}").Build();
        var w2 = new WalletBuilder().ForAvatar(avatarId).WithAddress($"b_{Guid.NewGuid():N}").Build();

        await _store.UpsertAsync(w1);
        await _store.UpsertAsync(w2);

        var result = await _store.GetAllAsync();

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        result.Result!.Count().Should().BeGreaterThanOrEqualTo(2);
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

    private static async Task<bool> ProbeSurrealAsync()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var r = await probe.GetAsync($"{SurrealTestDefaults.Endpoint}/health");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task BootstrapSchemaAsync()
    {
        using var ddlClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        ddlClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        ddlClient.DefaultRequestHeaders.Add("NS", _testNamespace);
        ddlClient.DefaultRequestHeaders.Add("DB", "test");

        // Inline DDL mirroring 010_wallet.surql (wave-1 schema).
        const string ddl = """
            DEFINE NAMESPACE IF NOT EXISTS $ns;
            USE NS $ns DB test;
            DEFINE DATABASE IF NOT EXISTS test;
            DEFINE TABLE IF NOT EXISTS wallet SCHEMAFULL;
            DEFINE FIELD IF NOT EXISTS id                    ON wallet TYPE string;
            DEFINE FIELD IF NOT EXISTS avatar_id             ON wallet TYPE string;
            DEFINE FIELD IF NOT EXISTS chain_type            ON wallet TYPE string;
            DEFINE FIELD IF NOT EXISTS address               ON wallet TYPE string;
            DEFINE FIELD IF NOT EXISTS public_key            ON wallet TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS label                 ON wallet TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS is_default            ON wallet TYPE bool DEFAULT false;
            DEFINE FIELD IF NOT EXISTS wallet_type           ON wallet TYPE string;
            DEFINE FIELD IF NOT EXISTS encrypted_private_key ON wallet TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS encrypted_seed_phrase ON wallet TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS created_date          ON wallet TYPE datetime
            """;

        var content = new StringContent(ddl, System.Text.Encoding.UTF8, "text/plain");
        _ = await ddlClient.PostAsync("/sql", content);
    }

    private async Task DropNamespaceAsync()
    {
        using var dropClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        dropClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        dropClient.DefaultRequestHeaders.Add("NS", _testNamespace);
        dropClient.DefaultRequestHeaders.Add("DB", "test");

        const string removeSql = "REMOVE NAMESPACE $ns";
        _ = await dropClient.PostAsync("/sql",
            new StringContent(removeSql, System.Text.Encoding.UTF8, "text/plain"));
    }
}
