using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealHolonStore"/>.
///
/// Each test creates an isolated SurrealDB namespace (test_{guid}) against
/// the test container on port 8442. Tests skip gracefully via <see cref="SkippableFact"/>
/// and <see cref="Skip.IfNot"/> when the SurrealDB container is unavailable, so the
/// test runner reports them as Skipped instead of Passed.
///
/// Required coverage:
///   1. Upsert → GetById round-trip (full holon with Metadata + PeerHolonIds)
///   2. GetById of non-existent id returns IsError=true
///   3. Upsert (update path) overwrites existing record
///   4. Delete removes the row
///   5. Delete of non-existent id returns IsError=true
///   6. QueryAsync with null query returns all holons
///   7. QueryAsync by AvatarId filters correctly
///   8. QueryAsync by ProviderName filters correctly
///   9. QueryAsync by ParentHolonId filters correctly
///  10. QueryAsync with multiple filters are AND-combined
/// </summary>
public sealed class SurrealHolonStoreTests : IAsyncLifetime
{
    // ── Connection config ─────────────────────────────────────────────────────

    // Connection config sourced from SurrealTestDefaults (points at local instance).

    // ── Per-instance state ────────────────────────────────────────────────────

    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealHolonStore _store = null!;
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
        _store = new SurrealHolonStore(executor);

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

    /// <summary>Test 1: Full round-trip including Metadata and PeerHolonIds.</summary>
    [SkippableFact]
    public async Task Upsert_ThenGetById_RoundTrips()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var peerId1 = Guid.NewGuid();
        var peerId2 = Guid.NewGuid();

        var holon = new Holon
        {
            Id            = Guid.NewGuid(),
            Name          = "Test Holon",
            Description   = "A holon for round-trip testing",
            ParentHolonId = Guid.NewGuid(),
            AvatarId      = Guid.NewGuid(),
            ProviderName  = "SurrealProvider",
            ChainId       = "algorand",
            AssetType     = "NFT",
            TokenId       = "tok-001",
            Metadata      = new Dictionary<string, string>
            {
                ["color"] = "blue",
                ["rarity"] = "rare"
            },
            PeerHolonIds  = new List<Guid> { peerId1, peerId2 },
            CreatedDate   = DateTime.UtcNow,
            ModifiedDate  = DateTime.UtcNow.AddSeconds(1),
            IsActive      = true
        };

        var upsertResult = await _store.UpsertAsync(holon);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");
        upsertResult.Result.Should().NotBeNull();

        var getResult = await _store.GetByIdAsync(holon.Id);

        getResult.IsError.Should().BeFalse();
        getResult.Message.Should().Be("Success");
        getResult.Result.Should().NotBeNull();

        var got = (Holon)getResult.Result!;
        got.Id.Should().Be(holon.Id);
        got.Name.Should().Be("Test Holon");
        got.Description.Should().Be("A holon for round-trip testing");
        got.ParentHolonId.Should().Be(holon.ParentHolonId);
        got.AvatarId.Should().Be(holon.AvatarId);
        got.ProviderName.Should().Be("SurrealProvider");
        got.ChainId.Should().Be("algorand");
        got.AssetType.Should().Be("NFT");
        got.TokenId.Should().Be("tok-001");
        got.IsActive.Should().BeTrue();
        got.Metadata.Should().ContainKey("color").WhoseValue.Should().Be("blue");
        got.Metadata.Should().ContainKey("rarity").WhoseValue.Should().Be("rare");
        got.PeerHolonIds.Should().HaveCount(2);
        got.PeerHolonIds.Should().Contain(peerId1);
        got.PeerHolonIds.Should().Contain(peerId2);
    }

    /// <summary>Test 2: GetById for a non-existent id returns IsError=true.</summary>
    [SkippableFact]
    public async Task GetById_NotFound_ReturnsError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.GetByIdAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("Holon not found.");
        result.Result.Should().BeNull();
    }

    /// <summary>Test 3: Upsert (update path) overwrites the existing record.</summary>
    [SkippableFact]
    public async Task Upsert_UpdatePath_OverwritesExistingRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var id       = Guid.NewGuid();
        var avatarId = Guid.NewGuid();

        var original = new Holon
        {
            Id           = id,
            AvatarId     = avatarId,
            Name         = "Original Name",
            Description  = "Original description",
            ProviderName = "Provider1",
            IsActive     = true,
            CreatedDate  = DateTime.UtcNow
        };
        await _store.UpsertAsync(original);

        var updated = new Holon
        {
            Id           = id,
            AvatarId     = avatarId,
            Name         = "Updated Name",
            Description  = "Updated description",
            ProviderName = "Provider1",
            IsActive     = false,
            CreatedDate  = original.CreatedDate,
            ModifiedDate = DateTime.UtcNow
        };
        var upsertResult = await _store.UpsertAsync(updated);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");

        var getResult = await _store.GetByIdAsync(id);
        getResult.IsError.Should().BeFalse();

        var got = (Holon)getResult.Result!;
        got.Name.Should().Be("Updated Name");
        got.Description.Should().Be("Updated description");
        got.IsActive.Should().BeFalse();
        got.ModifiedDate.Should().NotBeNull();
    }

    /// <summary>Test 4: Delete removes the record; subsequent GetById returns not-found.</summary>
    [SkippableFact]
    public async Task Delete_RemovesRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var holon = new Holon
        {
            Id           = Guid.NewGuid(),
            Name         = "To Delete",
            Description  = string.Empty,
            ProviderName = "SurrealProvider",
            IsActive     = true,
            CreatedDate  = DateTime.UtcNow
        };
        await _store.UpsertAsync(holon);

        var deleteResult = await _store.DeleteAsync(holon.Id);

        deleteResult.IsError.Should().BeFalse();
        deleteResult.Result.Should().BeTrue();
        deleteResult.Message.Should().Be("Deleted.");

        var getResult = await _store.GetByIdAsync(holon.Id);
        getResult.IsError.Should().BeTrue();
        getResult.Message.Should().Be("Holon not found.");
    }

    /// <summary>Test 5: Delete of a non-existent id returns IsError=true, Result=false.</summary>
    [SkippableFact]
    public async Task Delete_NotFound_ReturnsError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.DeleteAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().Be("Holon not found.");
    }

    /// <summary>Test 6: Null query returns all holons in the namespace.</summary>
    [SkippableFact]
    public async Task QueryAsync_NoFilters_ReturnsAll()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var h1 = new Holon
        {
            Id = Guid.NewGuid(), Name = "Holon A", Description = string.Empty,
            ProviderName = "ProvA", IsActive = true, CreatedDate = DateTime.UtcNow
        };
        var h2 = new Holon
        {
            Id = Guid.NewGuid(), Name = "Holon B", Description = string.Empty,
            ProviderName = "ProvB", IsActive = true, CreatedDate = DateTime.UtcNow
        };
        var h3 = new Holon
        {
            Id = Guid.NewGuid(), Name = "Holon C", Description = string.Empty,
            ProviderName = "ProvC", IsActive = false, CreatedDate = DateTime.UtcNow
        };

        await _store.UpsertAsync(h1);
        await _store.UpsertAsync(h2);
        await _store.UpsertAsync(h3);

        var result = await _store.QueryAsync(null);

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        result.Result!.Count().Should().BeGreaterThanOrEqualTo(3);
    }

    /// <summary>Test 7: QueryAsync by AvatarId returns only matching holons.</summary>
    [SkippableFact]
    public async Task QueryAsync_ByAvatarId_FiltersCorrectly()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var targetAvatar = Guid.NewGuid();
        var otherAvatar  = Guid.NewGuid();

        var h1 = new Holon
        {
            Id = Guid.NewGuid(), Name = "Holon1", Description = string.Empty,
            AvatarId = targetAvatar, ProviderName = "P", IsActive = true, CreatedDate = DateTime.UtcNow
        };
        var h2 = new Holon
        {
            Id = Guid.NewGuid(), Name = "Holon2", Description = string.Empty,
            AvatarId = targetAvatar, ProviderName = "P", IsActive = true, CreatedDate = DateTime.UtcNow
        };
        var h3 = new Holon
        {
            Id = Guid.NewGuid(), Name = "Holon3", Description = string.Empty,
            AvatarId = otherAvatar, ProviderName = "P", IsActive = true, CreatedDate = DateTime.UtcNow
        };

        await _store.UpsertAsync(h1);
        await _store.UpsertAsync(h2);
        await _store.UpsertAsync(h3);

        var result = await _store.QueryAsync(new HolonQueryRequest { AvatarId = targetAvatar });

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        var list = result.Result!.ToList();
        list.Should().HaveCount(2);
        list.All(h => h.AvatarId == targetAvatar).Should().BeTrue();
    }

    /// <summary>Test 8: QueryAsync by ProviderName returns only matching holons.</summary>
    [SkippableFact]
    public async Task QueryAsync_ByProviderName_FiltersCorrectly()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var providerName = $"Prov_{Guid.NewGuid():N}";

        var h1 = new Holon
        {
            Id = Guid.NewGuid(), Name = "H1", Description = string.Empty,
            ProviderName = providerName, IsActive = true, CreatedDate = DateTime.UtcNow
        };
        var h2 = new Holon
        {
            Id = Guid.NewGuid(), Name = "H2", Description = string.Empty,
            ProviderName = providerName, IsActive = true, CreatedDate = DateTime.UtcNow
        };
        var h3 = new Holon
        {
            Id = Guid.NewGuid(), Name = "H3", Description = string.Empty,
            ProviderName = "OtherProvider", IsActive = true, CreatedDate = DateTime.UtcNow
        };

        await _store.UpsertAsync(h1);
        await _store.UpsertAsync(h2);
        await _store.UpsertAsync(h3);

        var result = await _store.QueryAsync(new HolonQueryRequest { ProviderName = providerName });

        result.IsError.Should().BeFalse();
        var list = result.Result!.ToList();
        list.Should().HaveCount(2);
        list.All(h => h.ProviderName == providerName).Should().BeTrue();
    }

    /// <summary>Test 9: QueryAsync by ParentHolonId returns only matching holons.</summary>
    [SkippableFact]
    public async Task QueryAsync_ByParentHolonId_FiltersCorrectly()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var parentId = Guid.NewGuid();

        var h1 = new Holon
        {
            Id = Guid.NewGuid(), Name = "Child1", Description = string.Empty,
            ParentHolonId = parentId, ProviderName = "P", IsActive = true, CreatedDate = DateTime.UtcNow
        };
        var h2 = new Holon
        {
            Id = Guid.NewGuid(), Name = "Child2", Description = string.Empty,
            ParentHolonId = parentId, ProviderName = "P", IsActive = true, CreatedDate = DateTime.UtcNow
        };
        var h3 = new Holon
        {
            Id = Guid.NewGuid(), Name = "Orphan", Description = string.Empty,
            ParentHolonId = Guid.NewGuid(), ProviderName = "P", IsActive = true, CreatedDate = DateTime.UtcNow
        };

        await _store.UpsertAsync(h1);
        await _store.UpsertAsync(h2);
        await _store.UpsertAsync(h3);

        var result = await _store.QueryAsync(new HolonQueryRequest { ParentHolonId = parentId });

        result.IsError.Should().BeFalse();
        var list = result.Result!.ToList();
        list.Should().HaveCount(2);
        list.All(h => ((Holon)h).ParentHolonId == parentId).Should().BeTrue();
    }

    /// <summary>Test 10: Multiple filters are AND-combined — only rows matching all return.</summary>
    [SkippableFact]
    public async Task QueryAsync_MultipleFilters_AreAndCombined()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var avatarId     = Guid.NewGuid();
        var providerName = $"ProvAnd_{Guid.NewGuid():N}";

        // Matches both AvatarId AND ProviderName AND IsActive=true.
        var match1 = new Holon
        {
            Id = Guid.NewGuid(), Name = "Match1", Description = string.Empty,
            AvatarId = avatarId, ProviderName = providerName, IsActive = true, CreatedDate = DateTime.UtcNow
        };
        // Matches AvatarId but not ProviderName.
        var noMatch1 = new Holon
        {
            Id = Guid.NewGuid(), Name = "NoMatch1", Description = string.Empty,
            AvatarId = avatarId, ProviderName = "DifferentProvider", IsActive = true, CreatedDate = DateTime.UtcNow
        };
        // Matches ProviderName but not AvatarId.
        var noMatch2 = new Holon
        {
            Id = Guid.NewGuid(), Name = "NoMatch2", Description = string.Empty,
            AvatarId = Guid.NewGuid(), ProviderName = providerName, IsActive = true, CreatedDate = DateTime.UtcNow
        };
        // Matches AvatarId and ProviderName but IsActive=false.
        var noMatch3 = new Holon
        {
            Id = Guid.NewGuid(), Name = "NoMatch3", Description = string.Empty,
            AvatarId = avatarId, ProviderName = providerName, IsActive = false, CreatedDate = DateTime.UtcNow
        };

        await _store.UpsertAsync(match1);
        await _store.UpsertAsync(noMatch1);
        await _store.UpsertAsync(noMatch2);
        await _store.UpsertAsync(noMatch3);

        var result = await _store.QueryAsync(new HolonQueryRequest
        {
            AvatarId     = avatarId,
            ProviderName = providerName,
            IsActive     = true
        });

        result.IsError.Should().BeFalse();
        var list = result.Result!.ToList();
        list.Should().HaveCount(1);
        list[0].Id.Should().Be(match1.Id);
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task NftTransferReservation_ConcurrentClaimsOneWinner_FinalizesIdempotently()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var source = Guid.NewGuid();
        var targetA = Guid.NewGuid();
        var targetB = Guid.NewGuid();
        var holon = new Holon
        {
            Id = Guid.NewGuid(),
            AvatarId = source,
            Name = "Reserved NFT",
            ProviderName = "SurrealProvider",
            AssetType = "NFT",
            IsActive = true,
            CreatedDate = DateTime.UtcNow,
        };
        await _store.UpsertAsync(holon);

        var claimA = _store.TryReserveNftTransferAsync(holon.Id, source, targetA, "settlement-a");
        var claimB = _store.TryReserveNftTransferAsync(holon.Id, source, targetB, "settlement-b");
        var claims = await Task.WhenAll(claimA, claimB);

        claims.Should().OnlyContain(result => !result.IsError);
        claims.Count(result => result.Result).Should().Be(1);

        var winnerIsA = claims[0].Result;
        var winningTarget = winnerIsA ? targetA : targetB;
        var winningKey = winnerIsA ? "settlement-a" : "settlement-b";
        var losingTarget = winnerIsA ? targetB : targetA;
        var losingKey = winnerIsA ? "settlement-b" : "settlement-a";

        var executor = new DefaultSurrealExecutor(_connection);
        var reservationTimeQuery = SurrealQuery
            .Of("SELECT transfer_reserved_at FROM type::record($_t, $_id)")
            .WithParam("_t", "holon")
            .WithParam("_id", holon.Id.ToString("N"));
        var reservedAt = (await executor.QuerySingleAsync<TransferReservationProjection>(
            reservationTimeQuery)).TransferReservedAt;
        var reservationReplay = await _store.TryReserveNftTransferAsync(
            holon.Id, source, winningTarget, winningKey);
        var replayedReservedAt = (await executor.QuerySingleAsync<TransferReservationProjection>(
            reservationTimeQuery)).TransferReservedAt;
        reservationReplay.Result.Should().BeTrue();
        replayedReservedAt.Should().Be(reservedAt,
            "idempotent reservation replay must not refresh the original timestamp");

        var loserRetry = await _store.TryReserveNftTransferAsync(
            holon.Id, source, losingTarget, losingKey);
        loserRetry.Result.Should().BeFalse();

        var keyReuseWithDifferentTarget = await _store.TryReserveNftTransferAsync(
            holon.Id, source, losingTarget, winningKey);
        keyReuseWithDifferentTarget.Result.Should().BeFalse(
            "a settlement key cannot be rebound to different economics");

        var finalized = await _store.FinalizeReservedNftTransferAsync(
            holon.Id, source, winningTarget, winningKey);
        var replay = await _store.FinalizeReservedNftTransferAsync(
            holon.Id, source, winningTarget, winningKey);

        finalized.Result.Should().BeTrue();
        replay.Result.Should().BeTrue();
        (await _store.GetByIdAsync(holon.Id)).Result!.AvatarId.Should().Be(winningTarget);

        var historicalKeyReuse = await _store.TryReserveNftTransferAsync(
            holon.Id, winningTarget, Guid.NewGuid(), winningKey);
        historicalKeyReuse.Result.Should().BeFalse(
            "one settlement key cannot authorize a later transfer of the same NFT");
    }

    [SkippableFact]
    public async Task NftTransferReservation_ReleasePreservesSourceOwnership()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var source = Guid.NewGuid();
        var holon = new Holon
        {
            Id = Guid.NewGuid(),
            AvatarId = source,
            Name = "Released NFT",
            ProviderName = "SurrealProvider",
            AssetType = "NFT",
            IsActive = true,
            CreatedDate = DateTime.UtcNow,
        };
        await _store.UpsertAsync(holon);

        (await _store.TryReserveNftTransferAsync(
            holon.Id, source, Guid.NewGuid(), "settlement-release")).Result.Should().BeTrue();

        var released = await _store.ReleaseNftTransferReservationAsync(
            holon.Id, source, "settlement-release");
        var replay = await _store.ReleaseNftTransferReservationAsync(
            holon.Id, source, "settlement-release");

        released.Result.Should().BeTrue();
        replay.Result.Should().BeTrue();
        (await _store.GetByIdAsync(holon.Id)).Result!.AvatarId.Should().Be(source);
    }

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
        => SurrealTestSchema.BootstrapAsync(_testNamespace, "holon");

    private Task DropNamespaceAsync() => SurrealTestSchema.DropAsync(_testNamespace);

    private sealed class TransferReservationProjection
    {
        [System.Text.Json.Serialization.JsonPropertyName("transfer_reserved_at")]
        public DateTimeOffset? TransferReservedAt { get; set; }
    }
}
