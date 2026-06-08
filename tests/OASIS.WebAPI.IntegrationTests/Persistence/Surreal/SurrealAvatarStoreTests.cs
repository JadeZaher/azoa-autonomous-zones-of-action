using FluentAssertions;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Connection;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Providers.Stores.Surreal;

namespace OASIS.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealAvatarStore"/>.
///
/// Each test creates an isolated SurrealDB namespace (test_{guid}) against
/// the test container on port 8442. Tests skip gracefully via <see cref="SkippableFact"/>
/// and <see cref="Skip.IfNot"/> when the SurrealDB container is unavailable, so the
/// test runner reports them as Skipped instead of Passed.
///
/// Minimum coverage required:
///   1. Upsert → GetById round-trip
///   2. GetById of non-existent id returns IsError=true
///   3. Upsert (update path) overwrites existing record
///   4. Delete removes the row
///   5. Delete of non-existent id returns IsError=true
///   6. GetAll returns all inserted avatars
/// </summary>
public sealed class SurrealAvatarStoreTests : IAsyncLifetime
{
    // ── Connection config ─────────────────────────────────────────────────────

    // Connection config sourced from SurrealTestDefaults (points at local instance).

    // ── Per-instance state ────────────────────────────────────────────────────

    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealAvatarStore _store = null!;
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
        _store = new SurrealAvatarStore(executor);

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

    /// <summary>Test 1: Upsert creates an avatar; GetById retrieves it with matching fields.</summary>
    [SkippableFact]
    public async Task Upsert_ThenGetById_RoundTrips()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var avatar = new Avatar
        {
            Id           = Guid.NewGuid(),
            Username     = $"user_{Guid.NewGuid():N}",
            Email        = $"user_{Guid.NewGuid():N}@test.com",
            PasswordHash = "hashed_secret",
            Title        = "Dr.",
            FirstName    = "Alice",
            LastName     = "Smith",
            CreatedDate  = DateTime.UtcNow,
            IsActive     = true,
            IsVerified   = true,
            Karma        = 42,
            Level        = 3
        };

        var upsertResult = await _store.UpsertAsync(avatar);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");
        upsertResult.Result.Should().NotBeNull();

        var getResult = await _store.GetByIdAsync(avatar.Id);

        getResult.IsError.Should().BeFalse();
        getResult.Message.Should().Be("Success");
        getResult.Result.Should().NotBeNull();
        getResult.Result!.Id.Should().Be(avatar.Id);
        getResult.Result.Username.Should().Be(avatar.Username);
        getResult.Result.Email.Should().Be(avatar.Email);
        getResult.Result.Title.Should().Be("Dr.");
        getResult.Result.FirstName.Should().Be("Alice");
        getResult.Result.LastName.Should().Be("Smith");
        getResult.Result.IsVerified.Should().BeTrue();
        getResult.Result.Karma.Should().Be(42);
        getResult.Result.Level.Should().Be(3);
    }

    /// <summary>Test 2: GetById for a non-existent id returns IsError=true.</summary>
    [SkippableFact]
    public async Task GetById_NotFound_ReturnsError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.GetByIdAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("Avatar not found.");
        result.Result.Should().BeNull();
    }

    /// <summary>Test 3: Upsert (update path) overwrites the existing record.</summary>
    [SkippableFact]
    public async Task Upsert_UpdatePath_OverwritesExistingRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var id       = Guid.NewGuid();
        var username = $"user_{Guid.NewGuid():N}";
        var email    = $"user_{Guid.NewGuid():N}@test.com";

        var original = new Avatar
        {
            Id           = id,
            Username     = username,
            Email        = email,
            PasswordHash = "original_hash",
            FirstName    = "Bob",
            Karma        = 10,
            Level        = 1
        };
        await _store.UpsertAsync(original);

        // Overwrite with updated fields.
        var updated = new Avatar
        {
            Id           = id,
            Username     = username,
            Email        = email,
            PasswordHash = "updated_hash",
            FirstName    = "Robert",
            Karma        = 99,
            Level        = 5
        };
        var upsertResult = await _store.UpsertAsync(updated);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");

        var getResult = await _store.GetByIdAsync(id);
        getResult.IsError.Should().BeFalse();
        getResult.Result!.FirstName.Should().Be("Robert");
        getResult.Result.Karma.Should().Be(99);
        getResult.Result.Level.Should().Be(5);
    }

    /// <summary>Test 4: Delete removes the avatar; subsequent GetById returns not-found.</summary>
    [SkippableFact]
    public async Task Delete_RemovesRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var avatar = new Avatar
        {
            Id           = Guid.NewGuid(),
            Username     = $"user_{Guid.NewGuid():N}",
            Email        = $"user_{Guid.NewGuid():N}@test.com",
            PasswordHash = "some_hash"
        };

        await _store.UpsertAsync(avatar);

        var deleteResult = await _store.DeleteAsync(avatar.Id);

        deleteResult.IsError.Should().BeFalse();
        deleteResult.Result.Should().BeTrue();
        deleteResult.Message.Should().Be("Deleted.");

        var getResult = await _store.GetByIdAsync(avatar.Id);
        getResult.IsError.Should().BeTrue();
        getResult.Message.Should().Be("Avatar not found.");
    }

    /// <summary>Test 5: Delete of a non-existent id returns IsError=true, Result=false.</summary>
    [SkippableFact]
    public async Task Delete_NotFound_ReturnsError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.DeleteAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().Be("Avatar not found.");
    }

    /// <summary>Test 6: GetAll returns all avatars inserted in this namespace.</summary>
    [SkippableFact]
    public async Task GetAll_ReturnsAllInsertedAvatars()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var a1 = new Avatar
        {
            Id           = Guid.NewGuid(),
            Username     = $"user_{Guid.NewGuid():N}",
            Email        = $"user_{Guid.NewGuid():N}@test.com",
            PasswordHash = "hash1"
        };
        var a2 = new Avatar
        {
            Id           = Guid.NewGuid(),
            Username     = $"user_{Guid.NewGuid():N}",
            Email        = $"user_{Guid.NewGuid():N}@test.com",
            PasswordHash = "hash2"
        };

        await _store.UpsertAsync(a1);
        await _store.UpsertAsync(a2);

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

        // Inline DDL mirroring 090_avatar.surql (wave-2 schema).
        const string ddl = """
            DEFINE NAMESPACE IF NOT EXISTS $ns;
            USE NS $ns DB test;
            DEFINE DATABASE IF NOT EXISTS test;
            DEFINE TABLE IF NOT EXISTS avatar SCHEMAFULL;
            DEFINE FIELD IF NOT EXISTS id                  ON avatar TYPE string;
            DEFINE FIELD IF NOT EXISTS username            ON avatar TYPE string;
            DEFINE FIELD IF NOT EXISTS email               ON avatar TYPE string;
            DEFINE FIELD IF NOT EXISTS password_hash       ON avatar TYPE string;
            DEFINE FIELD IF NOT EXISTS title               ON avatar TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS first_name          ON avatar TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS last_name           ON avatar TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS created_date        ON avatar TYPE datetime;
            DEFINE FIELD IF NOT EXISTS last_beamed_in_date ON avatar TYPE option<datetime>;
            DEFINE FIELD IF NOT EXISTS is_active           ON avatar TYPE bool DEFAULT true;
            DEFINE FIELD IF NOT EXISTS is_verified         ON avatar TYPE bool DEFAULT false;
            DEFINE FIELD IF NOT EXISTS karma               ON avatar TYPE int DEFAULT 0;
            DEFINE FIELD IF NOT EXISTS level               ON avatar TYPE int DEFAULT 1
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
