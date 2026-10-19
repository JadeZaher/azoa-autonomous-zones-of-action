using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Auth;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>User-store contracts against isolated real schemas; see AGENTS.md §user-store-fixtures.</summary>
public sealed class SurrealUserStoreTests : IAsyncLifetime
{
    // ── Connection config ─────────────────────────────────────────────────────

    // Connection config sourced from SurrealTestDefaults (points at local instance).

    // ── Per-instance state ────────────────────────────────────────────────────

    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealUserStore _store = null!;
    private ISurrealConnection _connection = null!;
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

        _connection = new SurrealDbNetConnection(options);
        var executor = new DefaultSurrealExecutor(_connection);
        _store = new SurrealUserStore(executor);

        await BootstrapSchemaAsync();
        var migration = new WalletAuthIdentityMigrationService(executor);
        await migration.SweepAsync(default);
        (await migration.GetAsync(default)).Status.Should().Be("Ready");
    }

    public async Task DisposeAsync()
    {
        if (!_surrealAvailable || _connection is null) return;
        try { await DropNamespaceAsync(); }
        finally { _connection.Dispose(); }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>Test 1: Upsert creates a user; GetById retrieves it with matching fields.</summary>
    [SkippableFact]
    public async Task Upsert_ThenGetById_RoundTrips()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var user = new User
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
            IsVerified   = true
        };

        var upsertResult = await _store.UpsertAsync(user);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Result.Should().NotBeNull();
        upsertResult.Result!.Id.Should().Be(user.Id);

        var getResult = await _store.GetByIdAsync(user.Id);

        getResult.IsError.Should().BeFalse();
        getResult.Message.Should().Be("Success");
        getResult.Result.Should().NotBeNull();
        getResult.Result!.Id.Should().Be(user.Id);
        getResult.Result.Username.Should().Be(user.Username);
        getResult.Result.Email.Should().Be(user.Email);
        getResult.Result.Title.Should().Be("Dr.");
        getResult.Result.FirstName.Should().Be("Alice");
        getResult.Result.LastName.Should().Be("Smith");
        getResult.Result.IsVerified.Should().BeTrue();
    }

    /// <summary>Test 2: GetById for a non-existent id returns IsError=true.</summary>
    [SkippableFact]
    public async Task GetById_NotFound_ReturnsError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.GetByIdAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        // GetById returns a verbose, actionable not-found message (id + re-auth
        // hint); assert the stable prefix rather than the full diagnostic text.
        result.Message.Should().StartWith("User not found");
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

        var original = new User
        {
            Id           = id,
            Username     = username,
            Email        = email,
            PasswordHash = "original_hash",
            FirstName    = "Bob"
        };
        await _store.UpsertAsync(original);

        // Overwrite with updated fields.
        var updated = new User
        {
            Id           = id,
            Username     = username,
            Email        = email,
            PasswordHash = "original_hash",
            FirstName    = "Robert"
        };
        var upsertResult = await _store.UpsertAsync(updated);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Result.Should().NotBeNull();
        upsertResult.Result!.Id.Should().Be(id);

        var getResult = await _store.GetByIdAsync(id);
        getResult.IsError.Should().BeFalse();
        getResult.Result!.FirstName.Should().Be("Robert");
        getResult.Result.PasswordHash.Should().Be("original_hash");
    }

    /// <summary>Test 4: Delete removes the user; subsequent GetById returns not-found.</summary>
    [SkippableFact]
    public async Task Delete_RemovesRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var user = new User
        {
            Id           = Guid.NewGuid(),
            Username     = $"user_{Guid.NewGuid():N}",
            Email        = $"user_{Guid.NewGuid():N}@test.com",
            PasswordHash = "some_hash"
        };

        await _store.UpsertAsync(user);

        var deleteResult = await _store.DeleteAsync(user.Id);

        deleteResult.IsError.Should().BeFalse();
        deleteResult.Result.Should().BeTrue();

        var getResult = await _store.GetByIdAsync(user.Id);
        getResult.IsError.Should().BeTrue();
        getResult.Message.Should().StartWith("User not found");
    }

    /// <summary>Deleting an absent user succeeds without claiming a deletion.</summary>
    [SkippableFact]
    public async Task Delete_NotFound_SucceedsWithoutDeletingARow()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.DeleteAsync(Guid.NewGuid());

        result.IsError.Should().BeFalse();
        result.Result.Should().BeFalse();
    }

    /// <summary>Test 6: GetAll returns all users inserted in this namespace.</summary>
    [SkippableFact]
    public async Task GetAll_ReturnsAllInsertedUsers()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var a1 = new User
        {
            Id           = Guid.NewGuid(),
            Username     = $"user_{Guid.NewGuid():N}",
            Email        = $"user_{Guid.NewGuid():N}@test.com",
            PasswordHash = "hash1"
        };
        var a2 = new User
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

    // ── Tenant ownership (tenant-onboarding) ─────────────────────────────────

    /// <summary>Tenant fields round-trip: owner_tenant_id (record link),
    /// external_user_id, external_ref persist and reload intact.</summary>
    [SkippableFact]
    public async Task TenantFields_RoundTrip()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var tenantId = Guid.NewGuid();
        var child = new User
        {
            Id             = Guid.NewGuid(),
            Username       = $"user_{Guid.NewGuid():N}",
            Email          = $"user_{Guid.NewGuid():N}@test.com",
            PasswordHash   = "h",
            OwnerTenantId  = tenantId,
            ExternalUserId = "ext-1",
            ExternalRef    = "realm-a"
        };

        await _store.UpsertAsync(child);

        var got = await _store.GetByIdAsync(child.Id);
        got.IsError.Should().BeFalse();
        got.Result!.OwnerTenantId.Should().Be(tenantId);
        got.Result.ExternalUserId.Should().Be("ext-1");
        got.Result.ExternalRef.Should().Be("realm-a");
    }

    /// <summary>ListByOwnerTenant is owner-scoped: T1 sees only its own children,
    /// never T2's. This is acceptance (c) — cross-tenant isolation — proven at
    /// the store layer.</summary>
    [SkippableFact]
    public async Task ListByOwnerTenant_IsOwnerScoped_NoCrossTenantLeak()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        var c1 = NewChild(t1, "u-a");
        var c2 = NewChild(t1, "u-b");
        var c3 = NewChild(t2, "u-c");
        await _store.UpsertAsync(c1);
        await _store.UpsertAsync(c2);
        await _store.UpsertAsync(c3);

        var t1Children = await _store.ListByOwnerTenantAsync(t1);
        t1Children.IsError.Should().BeFalse();
        var ids = t1Children.Result!.Select(a => a.Id).ToList();
        ids.Should().Contain(new[] { c1.Id, c2.Id });
        ids.Should().NotContain(c3.Id); // T2's child is never visible to T1.

        var t2Children = await _store.ListByOwnerTenantAsync(t2);
        t2Children.Result!.Select(a => a.Id).Should().Contain(c3.Id).And.NotContain(c1.Id);
    }

    /// <summary>GetByTenantAndExternalUser resolves only within the tenant, and
    /// returns Result==null (no error) on a miss so the manager treats it as
    /// "create new" (idempotency seam).</summary>
    [SkippableFact]
    public async Task GetByTenantAndExternalUser_ScopedResolve_AndMissIsNullNoError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        var c1 = NewChild(t1, "shared-id");
        var c2 = NewChild(t2, "shared-id"); // same external id, different tenant — allowed.
        await _store.UpsertAsync(c1);
        await _store.UpsertAsync(c2);

        var r1 = await _store.GetByTenantAndExternalUserAsync(t1, "shared-id");
        r1.IsError.Should().BeFalse();
        r1.Result!.Id.Should().Be(c1.Id); // resolves T1's, not T2's.

        // A miss: existing external id under a tenant that has no such child.
        var miss = await _store.GetByTenantAndExternalUserAsync(Guid.NewGuid(), "shared-id");
        miss.IsError.Should().BeFalse();
        miss.Result.Should().BeNull();
    }

    private static User NewChild(Guid tenantId, string externalUserId) => new()
    {
        Id             = Guid.NewGuid(),
        Username       = $"user_{Guid.NewGuid():N}",
        Email          = $"user_{Guid.NewGuid():N}@test.com",
        PasswordHash   = "h",
        OwnerTenantId  = tenantId,
        ExternalUserId = externalUserId
    };

    // ── Infrastructure ────────────────────────────────────────────────────────

    private static async Task<bool> ProbeSurrealAsync()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var r = await probe.GetAsync($"{SurrealTestDefaults.Endpoint}/health");
            return r.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    private Task BootstrapSchemaAsync()
        => SurrealTestSchema.BootstrapAsync(_testNamespace, "user", "wallet_auth_identity_migration");

    private Task DropNamespaceAsync() => SurrealTestSchema.DropAsync(_testNamespace);
}
