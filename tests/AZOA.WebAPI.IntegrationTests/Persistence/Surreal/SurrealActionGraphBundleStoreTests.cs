using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealActionGraphBundleStore"/>.
///
/// Each test creates an isolated SurrealDB namespace (test_{guid}) against
/// the test container on port 8442. Tests skip gracefully via <see cref="SkippableFact"/>
/// and <see cref="Skip.IfNot"/> when the SurrealDB container is unavailable, so the
/// test runner reports them as Skipped instead of Passed.
///
/// Required coverage:
///   1. Upsert → GetById round-trip (full ActionGraphBundle, BoundHolonIds populated)
///   2. GetById of non-existent id returns IsError=true
///   3. Upsert (update path) overwrites existing record
///   4. Delete removes the record
///   5. Delete of non-existent id returns IsError=true
///   6. GetAll returns all inserted records
///   7. BoundHolonIds (empty list) + nullable fields (null and set) round-trip
/// </summary>
public sealed class SurrealActionGraphBundleStoreTests : IAsyncLifetime
{
    // ── Connection config ─────────────────────────────────────────────────────

    // Connection config sourced from SurrealTestDefaults (points at local instance).

    // ── Per-instance state ────────────────────────────────────────────────────

    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealActionGraphBundleStore _store = null!;
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
        _store = new SurrealActionGraphBundleStore(executor);

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

    /// <summary>Test 1: Upsert creates an action graph bundle with BoundHolonIds; GetById retrieves it with matching fields.</summary>
    [SkippableFact]
    public async Task Upsert_ThenGetById_RoundTrips()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var holonId1 = Guid.NewGuid();
        var holonId2 = Guid.NewGuid();

        var odk = new ActionGraphBundle
        {
            Id               = Guid.NewGuid(),
            Name             = "Test action graph bundle",
            Description      = "A test action graph bundle",
            PublicKey        = "pk_test_abc123",
            PrivateKeyHash   = "hash_test_xyz789",
            UserId         = Guid.NewGuid(),
            BoundHolonIds    = new List<Guid> { holonId1, holonId2 },
            TargetChain      = "Algorand",
            GeneratedCode    = "// generated code placeholder",
            DeploymentConfig = "{\"env\":\"test\"}",
            CreatedDate      = DateTime.UtcNow,
            ModifiedDate     = null,
            IsActive         = true
        };

        var upsertResult = await _store.UpsertAsync(odk);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");
        upsertResult.Result.Should().NotBeNull();

        var getResult = await _store.GetByIdAsync(odk.Id);

        getResult.IsError.Should().BeFalse();
        getResult.Message.Should().Be("Success");
        getResult.Result.Should().NotBeNull();
        getResult.Result!.Id.Should().Be(odk.Id);
        getResult.Result.Name.Should().Be("Test action graph bundle");
        getResult.Result.Description.Should().Be("A test action graph bundle");
        getResult.Result.PublicKey.Should().Be("pk_test_abc123");
        getResult.Result.PrivateKeyHash.Should().Be("hash_test_xyz789");
        getResult.Result.UserId.Should().Be(odk.UserId);
        getResult.Result.TargetChain.Should().Be("Algorand");
        getResult.Result.GeneratedCode.Should().Be("// generated code placeholder");
        getResult.Result.DeploymentConfig.Should().Be("{\"env\":\"test\"}");
        getResult.Result.IsActive.Should().BeTrue();

        var returnedIds = getResult.Result.BoundHolonIds;
        returnedIds.Should().HaveCount(2);
        returnedIds.Should().Contain(holonId1);
        returnedIds.Should().Contain(holonId2);
    }

    /// <summary>Test 2: GetById for a non-existent id returns IsError=true.</summary>
    [SkippableFact]
    public async Task GetById_NotFound_ReturnsError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.GetByIdAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("action graph bundle not found.");
        result.Result.Should().BeNull();
    }

    /// <summary>Test 3: Upsert (update path) overwrites the existing record.</summary>
    [SkippableFact]
    public async Task Upsert_UpdatePath_OverwritesExistingRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var id = Guid.NewGuid();

        var original = new ActionGraphBundle
        {
            Id          = id,
            Name        = "Original Name",
            Description = "Original description",
            TargetChain = "Ethereum",
            CreatedDate = DateTime.UtcNow,
            IsActive    = true
        };
        await _store.UpsertAsync(original);

        var updated = new ActionGraphBundle
        {
            Id          = id,
            Name        = "Updated Name",
            Description = "Updated description",
            TargetChain = "Algorand",
            CreatedDate = original.CreatedDate,
            ModifiedDate = DateTime.UtcNow,
            IsActive    = false
        };
        var upsertResult = await _store.UpsertAsync(updated);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");

        var getResult = await _store.GetByIdAsync(id);
        getResult.IsError.Should().BeFalse();
        getResult.Result!.Name.Should().Be("Updated Name");
        getResult.Result.Description.Should().Be("Updated description");
        getResult.Result.TargetChain.Should().Be("Algorand");
        getResult.Result.IsActive.Should().BeFalse();
        getResult.Result.ModifiedDate.Should().NotBeNull();
    }

    /// <summary>Test 4: Delete removes the record; subsequent GetById returns not-found.</summary>
    [SkippableFact]
    public async Task Delete_RemovesRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var odk = new ActionGraphBundle
        {
            Id          = Guid.NewGuid(),
            Name        = "To Delete",
            Description = "This will be deleted",
            CreatedDate = DateTime.UtcNow,
            IsActive    = true
        };

        await _store.UpsertAsync(odk);

        var deleteResult = await _store.DeleteAsync(odk.Id);

        deleteResult.IsError.Should().BeFalse();
        deleteResult.Result.Should().BeTrue();
        deleteResult.Message.Should().Be("Deleted.");

        var getResult = await _store.GetByIdAsync(odk.Id);
        getResult.IsError.Should().BeTrue();
        getResult.Message.Should().Be("action graph bundle not found.");
    }

    /// <summary>Test 5: Delete of a non-existent id returns IsError=true, Result=false.</summary>
    [SkippableFact]
    public async Task Delete_NotFound_ReturnsError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.DeleteAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().Be("action graph bundle not found.");
    }

    /// <summary>Test 6: GetAll returns all inserted action graph bundle records.</summary>
    [SkippableFact]
    public async Task GetAll_ReturnsAllInsertedActionGraphBundles()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var odk1 = new ActionGraphBundle
        {
            Id          = Guid.NewGuid(),
            Name        = "ActionGraphBundle One",
            Description = "First bundle",
            CreatedDate = DateTime.UtcNow,
            IsActive    = true
        };
        var odk2 = new ActionGraphBundle
        {
            Id          = Guid.NewGuid(),
            Name        = "ActionGraphBundle Two",
            Description = "Second bundle",
            CreatedDate = DateTime.UtcNow,
            IsActive    = true
        };

        await _store.UpsertAsync(odk1);
        await _store.UpsertAsync(odk2);

        var result = await _store.GetAllAsync();

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        result.Result!.Count().Should().BeGreaterThanOrEqualTo(2);

        var ids = result.Result!.Select(o => o.Id).ToList();
        ids.Should().Contain(odk1.Id);
        ids.Should().Contain(odk2.Id);
    }

    /// <summary>
    /// Test 7: Empty BoundHolonIds list survives round-trip;
    /// nullable PublicKey/PrivateKeyHash/UserId round-trip both null and set.
    /// </summary>
    [SkippableFact]
    public async Task Upsert_PreservesBoundHolonIdsAndNullableFields()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        // Record with empty list and all nullables null.
        var odkNulls = new ActionGraphBundle
        {
            Id               = Guid.NewGuid(),
            Name             = "Null Fields ActionGraphBundle",
            Description      = string.Empty,
            PublicKey        = null,
            PrivateKeyHash   = null,
            UserId         = null,
            BoundHolonIds    = new List<Guid>(),
            TargetChain      = null,
            GeneratedCode    = null,
            DeploymentConfig = null,
            CreatedDate      = DateTime.UtcNow,
            ModifiedDate     = null,
            IsActive         = true
        };
        await _store.UpsertAsync(odkNulls);

        var nullsResult = await _store.GetByIdAsync(odkNulls.Id);
        nullsResult.IsError.Should().BeFalse();
        var nullsRt = nullsResult.Result!;
        nullsRt.PublicKey.Should().BeNull();
        nullsRt.PrivateKeyHash.Should().BeNull();
        nullsRt.UserId.Should().BeNull();
        nullsRt.BoundHolonIds.Should().BeEmpty();
        nullsRt.TargetChain.Should().BeNull();

        // Record with all nullables populated and non-empty BoundHolonIds.
        var userId = Guid.NewGuid();
        var holonId  = Guid.NewGuid();

        var odkPopulated = new ActionGraphBundle
        {
            Id               = Guid.NewGuid(),
            Name             = "Populated Fields ActionGraphBundle",
            Description      = "desc",
            PublicKey        = "pub-key",
            PrivateKeyHash   = "priv-hash",
            UserId         = userId,
            BoundHolonIds    = new List<Guid> { holonId },
            TargetChain      = "Solana",
            GeneratedCode    = "code",
            DeploymentConfig = "cfg",
            CreatedDate      = DateTime.UtcNow,
            ModifiedDate     = DateTime.UtcNow,
            IsActive         = false
        };
        await _store.UpsertAsync(odkPopulated);

        var popResult = await _store.GetByIdAsync(odkPopulated.Id);
        popResult.IsError.Should().BeFalse();
        var popRt = popResult.Result!;
        popRt.PublicKey.Should().Be("pub-key");
        popRt.PrivateKeyHash.Should().Be("priv-hash");
        popRt.UserId.Should().Be(userId);
        popRt.BoundHolonIds.Should().HaveCount(1);
        popRt.BoundHolonIds.Should().Contain(holonId);
        popRt.TargetChain.Should().Be("Solana");
        popRt.GeneratedCode.Should().Be("code");
        popRt.DeploymentConfig.Should().Be("cfg");
        popRt.ModifiedDate.Should().NotBeNull();
        popRt.IsActive.Should().BeFalse();
    }

    /// <summary>
    /// Test 8: GetByNameAndUserAsync matches owner-scoped records (case-insensitive)
    /// and returns Result=null without IsError when no record is owned by the user.
    /// This is the IDOR-safe lookup that <see cref="ActionGraphBundleManager.CreateOrUpdateAsync"/>
    /// uses on POST.
    /// </summary>
    [SkippableFact]
    public async Task GetByNameAndUserAsync_ScopesByOwnerAndIsCaseInsensitive()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var ownedByA = new ActionGraphBundle
        {
            Id          = Guid.NewGuid(),
            Name        = "Shared Name",
            Description = "A's data",
            UserId    = userA,
            CreatedDate = DateTime.UtcNow,
            IsActive    = true
        };
        var ownedByB = new ActionGraphBundle
        {
            Id          = Guid.NewGuid(),
            Name        = "Shared Name",
            Description = "B's data",
            UserId    = userB,
            CreatedDate = DateTime.UtcNow,
            IsActive    = true
        };
        await _store.UpsertAsync(ownedByA);
        await _store.UpsertAsync(ownedByB);

        // Case-insensitive match scoped to A.
        var resultA = await _store.GetByNameAndUserAsync("shared name", userA);
        resultA.IsError.Should().BeFalse();
        resultA.Result.Should().NotBeNull();
        resultA.Result!.Id.Should().Be(ownedByA.Id, "name lookup must be scoped to the calling user");
        resultA.Result.Description.Should().Be("A's data");

        // Same name -> B's record for user B.
        var resultB = await _store.GetByNameAndUserAsync("Shared Name", userB);
        resultB.Result!.Id.Should().Be(ownedByB.Id);

        // A stranger never sees either record.
        var resultStranger = await _store.GetByNameAndUserAsync("Shared Name", Guid.NewGuid());
        resultStranger.IsError.Should().BeFalse();
        resultStranger.Result.Should().BeNull();
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

    private Task BootstrapSchemaAsync()
        => SurrealTestSchema.BootstrapAsync(_testNamespace, "action_graph_bundle");

    private Task DropNamespaceAsync() => SurrealTestSchema.DropAsync(_testNamespace);
}
