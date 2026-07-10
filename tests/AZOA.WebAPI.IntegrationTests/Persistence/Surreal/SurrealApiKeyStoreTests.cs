using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

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

    // ── scope CSV round-trip (regression: colon-shaped scopes) ───────────────────

    // Root-cause regression guard. On SurrealDB 3.x the api_key.scopes field was
    // TYPE option<string> (`none | string`); a colon-shaped CSV value like
    // "dapp:develop,dapp:manage" was mis-coerced into the union as a record-id
    // (table:id) and the CREATE failed with:
    //   Couldn't coerce value for field `scopes`: Expected `none | string`
    //   but found `dapp:develop`
    // The field is now TYPE string (empty "" = full access). This test would have
    // caught the production bug — the pre-existing suite only ever wrote the
    // colon-FREE CSV "read,write", which never triggers record-id detection.
    [SkippableFact]
    public async Task Create_WithColonBearingScopesAndOrigins_RoundTripsIntact()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var owner = Guid.NewGuid();
        var key = NewApiKey(avatarId: owner);
        key.Scopes = "dapp:develop,dapp:manage";
        key.AllowedOrigins = "https://x.example:443,https://y.example:8443";

        await _store.CreateAsync(key, CancellationToken.None);

        var byHash = await _store.GetByHashAsync(key.KeyHash, CancellationToken.None);
        byHash.Should().NotBeNull();
        byHash!.Scopes.Should().Be("dapp:develop,dapp:manage",
            "colon-shaped scope CSV must persist verbatim, not be coerced to a record-id");
        byHash.AllowedOrigins.Should().Be("https://x.example:443,https://y.example:8443",
            "colon/URL-bearing allowed_origins CSV must persist verbatim");

        // Same value must survive the record-id read path too.
        var byId = await _store.GetByIdForAvatarAsync(key.Id, owner, CancellationToken.None);
        byId.Should().NotBeNull();
        byId!.Scopes.Should().Be("dapp:develop,dapp:manage");
        byId.AllowedOrigins.Should().Be("https://x.example:443,https://y.example:8443");
    }

    // The "empty/NONE scopes = full-access legacy key" invariant: a domain-null
    // Scopes/AllowedOrigins persists as "" and reads back as null (not ""), so the
    // full-access carve-out in ApiKeyController / ApiKeyAuthenticationHandler /
    // AzoaScopes keeps working after the option<string> -> string schema change.
    [SkippableFact]
    public async Task Create_WithNullScopes_RoundTripsAsNull_FullAccessLegacyPreserved()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var owner = Guid.NewGuid();
        var key = NewApiKey(avatarId: owner);
        key.Scopes = null;
        key.AllowedOrigins = null;

        await _store.CreateAsync(key, CancellationToken.None);

        var hit = await _store.GetByHashAsync(key.KeyHash, CancellationToken.None);
        hit.Should().NotBeNull();
        hit!.Scopes.Should().BeNull("empty stored scopes must read back as domain null = full access");
        hit.AllowedOrigins.Should().BeNull("empty stored allowed_origins must read back as domain null = all allowed");
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
        KeyPrefix   = "azoa_test12345",
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
    private Task BootstrapSchemaAsync()
        // Apply the REAL generated golden for api_key (avatar_id is record<avatar>
        // there, not the stale `string` this test used to hand-roll — the drift
        // broke SurrealDB-3.x record coercion).
        => SurrealTestSchema.BootstrapAsync(_testNamespace, "api_key");

    private Task DropNamespaceAsync() => SurrealTestSchema.DropAsync(_testNamespace);
}
