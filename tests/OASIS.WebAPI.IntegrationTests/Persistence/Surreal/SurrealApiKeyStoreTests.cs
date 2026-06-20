using FluentAssertions;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Connection;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Providers.Stores.Surreal;

namespace OASIS.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealApiKeyStore"/>.
///
/// Each test creates an isolated SurrealDB namespace (test_{guid}) against the
/// test container on port 8442. Tests skip gracefully via <see cref="SkippableFact"/>
/// and <see cref="Skip.IfNot"/> when the SurrealDB container is unavailable, so
/// the test runner reports them as Skipped instead of Passed.
/// </summary>
public sealed class SurrealApiKeyStoreTests : IAsyncLifetime
{
    // Connection config sourced from SurrealTestDefaults (points at local instance).

    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealApiKeyStore _store = null!;
    private HttpSurrealConnection _connection = null!;
    private bool _surrealAvailable;

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
            Password  = SurrealTestDefaults.Password,
        };

        var http = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        _connection = new HttpSurrealConnection(http, options);
        var executor = new DefaultSurrealExecutor(_connection);
        _store = new SurrealApiKeyStore(executor);

        await BootstrapSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_surrealAvailable || _connection is null) return;
        try { await DropNamespaceAsync(); } catch { /* best-effort teardown */ }
        finally { _connection.Dispose(); }
    }

    [SkippableFact]
    public async Task Create_PersistsApiKey_RetrievableByHash()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var key = NewApiKey(keyHash: $"hash-{Guid.NewGuid():N}");

        await _store.CreateAsync(key, CancellationToken.None);
        var hit = await _store.GetByHashAsync(key.KeyHash, CancellationToken.None);

        hit.Should().NotBeNull();
        hit!.Id.Should().Be(key.Id);
        hit.KeyHash.Should().Be(key.KeyHash);
        hit.AvatarId.Should().Be(key.AvatarId);
        hit.IsActive.Should().BeTrue();
    }

    [SkippableFact]
    public async Task GetByHash_UnknownHash_ReturnsNull()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var hit = await _store.GetByHashAsync($"missing-{Guid.NewGuid():N}", CancellationToken.None);
        hit.Should().BeNull();
    }

    [SkippableFact]
    public async Task Create_DuplicateHash_ThrowsOnUniqueViolation()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var sharedHash = $"dup-{Guid.NewGuid():N}";
        await _store.CreateAsync(NewApiKey(keyHash: sharedHash), CancellationToken.None);

        var act = async () => await _store.CreateAsync(
            NewApiKey(keyHash: sharedHash), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SurrealApiKeyStore.CreateAsync failed*");
    }

    [SkippableFact]
    public async Task ListByAvatar_ReturnsOwnedKeysOnly_NewestFirst()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();

        var k1 = NewApiKey(avatarId: owner, createdDate: DateTime.UtcNow.AddMinutes(-10));
        var k2 = NewApiKey(avatarId: owner, createdDate: DateTime.UtcNow);
        var k3 = NewApiKey(avatarId: other);

        await _store.CreateAsync(k1, CancellationToken.None);
        await _store.CreateAsync(k2, CancellationToken.None);
        await _store.CreateAsync(k3, CancellationToken.None);

        var list = await _store.ListByAvatarAsync(owner, CancellationToken.None);

        list.Should().HaveCount(2);
        list[0].Id.Should().Be(k2.Id, "ORDER BY created_date DESC returns newest first");
        list[1].Id.Should().Be(k1.Id);
        list.Should().NotContain(k => k.AvatarId == other);
    }

    [SkippableFact]
    public async Task GetByIdForAvatar_WrongAvatar_ReturnsNull()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var owner = Guid.NewGuid();
        var intruder = Guid.NewGuid();
        var key = NewApiKey(avatarId: owner);

        await _store.CreateAsync(key, CancellationToken.None);

        var ownerHit = await _store.GetByIdForAvatarAsync(key.Id, owner, CancellationToken.None);
        var intruderHit = await _store.GetByIdForAvatarAsync(key.Id, intruder, CancellationToken.None);

        ownerHit.Should().NotBeNull();
        intruderHit.Should().BeNull("cross-avatar reads must look like 'not found'");
    }

    [SkippableFact]
    public async Task Revoke_SetsRevokedAtAndIsActiveFalse_ReturnsTrue()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var owner = Guid.NewGuid();
        var key = NewApiKey(avatarId: owner);
        await _store.CreateAsync(key, CancellationToken.None);

        var revokedAt = DateTime.UtcNow;
        var ok = await _store.RevokeAsync(key.Id, owner, revokedAt, CancellationToken.None);

        ok.Should().BeTrue();
        var after = await _store.GetByIdForAvatarAsync(key.Id, owner, CancellationToken.None);
        after.Should().NotBeNull();
        after!.IsActive.Should().BeFalse();
        after.RevokedAt.Should().NotBeNull();
        after.RevokedAt!.Value.Should().BeCloseTo(revokedAt, TimeSpan.FromSeconds(2));
    }

    [SkippableFact]
    public async Task Revoke_WrongAvatar_ReturnsFalse_RowUnchanged()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var owner = Guid.NewGuid();
        var intruder = Guid.NewGuid();
        var key = NewApiKey(avatarId: owner);
        await _store.CreateAsync(key, CancellationToken.None);

        var ok = await _store.RevokeAsync(key.Id, intruder, DateTime.UtcNow, CancellationToken.None);

        ok.Should().BeFalse();
        var after = await _store.GetByIdForAvatarAsync(key.Id, owner, CancellationToken.None);
        after.Should().NotBeNull();
        after!.IsActive.Should().BeTrue("intruder revoke must be a no-op");
        after.RevokedAt.Should().BeNull();
    }

    [SkippableFact]
    public async Task Delete_HardRemovesRow_ReturnsTrue()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var owner = Guid.NewGuid();
        var key = NewApiKey(avatarId: owner);
        await _store.CreateAsync(key, CancellationToken.None);

        var ok = await _store.DeleteAsync(key.Id, owner, CancellationToken.None);

        ok.Should().BeTrue();
        var after = await _store.GetByIdForAvatarAsync(key.Id, owner, CancellationToken.None);
        after.Should().BeNull();
    }

    [SkippableFact]
    public async Task TouchLastUsedAsync_NeverThrows_EvenForMissingRow()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        // Contract: must not throw under any failure mode. Verify with a row
        // that does not exist — the UPDATE matches zero rows but the call must
        // still complete cleanly.
        var act = async () =>
            await _store.TouchLastUsedAsync(Guid.NewGuid(), DateTime.UtcNow, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [SkippableFact]
    public async Task TouchLastUsedAsync_UpdatesTimestamp_OnExistingRow()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var owner = Guid.NewGuid();
        var key = NewApiKey(avatarId: owner);
        await _store.CreateAsync(key, CancellationToken.None);

        var ts = DateTime.UtcNow;
        await _store.TouchLastUsedAsync(key.Id, ts, CancellationToken.None);

        var after = await _store.GetByIdForAvatarAsync(key.Id, owner, CancellationToken.None);
        after.Should().NotBeNull();
        after!.LastUsedAt.Should().NotBeNull();
        after.LastUsedAt!.Value.Should().BeCloseTo(ts, TimeSpan.FromSeconds(2));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ApiKey NewApiKey(
        Guid? avatarId = null,
        string? keyHash = null,
        DateTime? createdDate = null) => new()
    {
        Id          = Guid.NewGuid(),
        AvatarId    = avatarId ?? Guid.NewGuid(),
        Name        = "test-key",
        KeyHash     = keyHash ?? $"hash-{Guid.NewGuid():N}",
        KeyPrefix   = "oasis_test12345",
        CreatedDate = createdDate ?? DateTime.UtcNow,
        IsActive    = true,
        Scopes      = "read,write",
    };

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

    /// <summary>
    /// Applies the minimal DDL for <c>api_key</c> matching
    /// the committed schema at <c>Persistence/SurrealDb/Generated/Schemas/api_key.surql</c>
    /// (authored from <c>Persistence/SurrealDb/Models/ApiKey.cs</c>).
    /// </summary>
    private async Task BootstrapSchemaAsync()
    {
        using var ddlClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        ddlClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        // SurrealDB 3.x requires "Surreal-NS"/"Surreal-DB" headers; scope the
        // DDL client to the per-test namespace so the table DDL lands there.
        ddlClient.DefaultRequestHeaders.Add("Surreal-NS", _testNamespace);
        ddlClient.DefaultRequestHeaders.Add("Surreal-DB", "test");

        // The namespace/database identifiers cannot be $params in DDL. Create
        // the namespace at ROOT, then the database scoped to the namespace with
        // the NS header ONLY (a Surreal-DB header naming a not-yet-existing db
        // makes the connection-level USE fail). The table DDL below then runs
        // with both NS+DB headers set.
        using (var nsClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) })
        {
            nsClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
            await nsClient.PostAsync("/sql", new StringContent(
                $"DEFINE NAMESPACE IF NOT EXISTS {_testNamespace}", System.Text.Encoding.UTF8, "text/plain"));
            nsClient.DefaultRequestHeaders.Add("Surreal-NS", _testNamespace);
            await nsClient.PostAsync("/sql", new StringContent(
                "DEFINE DATABASE IF NOT EXISTS test", System.Text.Encoding.UTF8, "text/plain"));
        }

        const string ddl = """
            DEFINE TABLE IF NOT EXISTS api_key SCHEMAFULL;
            DEFINE FIELD IF NOT EXISTS id           ON api_key TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS avatar_id    ON api_key TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS name         ON api_key TYPE string;
            DEFINE FIELD IF NOT EXISTS key_hash     ON api_key TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS key_prefix   ON api_key TYPE string;
            DEFINE FIELD IF NOT EXISTS created_date ON api_key TYPE datetime;
            DEFINE FIELD IF NOT EXISTS expires_at   ON api_key TYPE option<datetime>;
            DEFINE FIELD IF NOT EXISTS last_used_at ON api_key TYPE option<datetime>;
            DEFINE FIELD IF NOT EXISTS revoked_at   ON api_key TYPE option<datetime>;
            DEFINE FIELD IF NOT EXISTS is_active    ON api_key TYPE bool DEFAULT true;
            DEFINE FIELD IF NOT EXISTS scopes       ON api_key TYPE option<string>;
            DEFINE INDEX IF NOT EXISTS api_key_unique_hash ON api_key FIELDS key_hash UNIQUE;
            DEFINE INDEX IF NOT EXISTS api_key_by_avatar   ON api_key FIELDS avatar_id
            """;

        var content = new StringContent(ddl, System.Text.Encoding.UTF8, "text/plain");
        var response = await ddlClient.PostAsync("/sql", content);
        _ = response; // best-effort
    }

    private async Task DropNamespaceAsync()
    {
        using var dropClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        dropClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
                var removeSql = $"REMOVE NAMESPACE IF EXISTS {_testNamespace}";
        var content = new StringContent(removeSql, System.Text.Encoding.UTF8, "text/plain");
        await dropClient.PostAsync("/sql", content);
    }
}
