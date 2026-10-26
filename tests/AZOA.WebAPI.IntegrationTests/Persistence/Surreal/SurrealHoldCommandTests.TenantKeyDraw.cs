using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// F18: a plain tenant API key (no act_as_tenant claim, so a null credential tenant) opening a
/// reserve hold, the shape ArdaNova's AzoaHoldClient sends. See Services/Hold/AGENTS.md §tenant-key-draw.
/// </summary>
public sealed partial class SurrealHoldCommandTests
{
    [Fact]
    public async Task PlainTenantKeyOpensAHoldAgainstItsOwnRegisteredPartition()
    {
        var authority = await PlainTenantKeyAuthorityAsync();

        (await _store.CommitLocalAsync(Open() with { RequestKey = "open:award-1", HoldId = "award-1" }, authority))
            .Should().NotBeNull();

        (await Counters()).Held.Should().Be(400);
    }

    [Fact]
    public async Task PlainTenantKeyCannotDrawAnotherTenantsPartitionOrThePlatformUnit()
    {
        var authority = await PlainTenantKeyAuthorityAsync();
        await SaveReserveAsync("43", "user:" + Guid.NewGuid().ToString("N"));
        await SaveReserveAsync("44", tenantId: null);

        foreach (var assetId in new[] { "43", "44" })
        {
            var asset = new HoldAssetIdentity("Algorand", ChainNetwork.Devnet, "genesis", assetId, "reserve");
            var foreign = Open() with { RequestKey = "open:foreign-" + assetId, HoldId = "foreign-" + assetId, Asset = asset };
            foreign = foreign with { Definition = foreign.Definition! with { Asset = asset } };

            (await _store.CommitLocalAsync(foreign, authority)).Should().BeNull(
                "asset {0} is not this key owner's registered budget", assetId);
            (await CountersFor(assetId)).Held.Should().Be(0);
        }
    }

    /// <summary>F31: ArdaNova's approve, a reserve continuation under a plain tenant key, replays only byte-identically.</summary>
    [Fact]
    public async Task PlainTenantKeyContinuationCompletesAndReplaysOnlyItsExactWindow()
    {
        var authority = await PlainTenantKeyAuthorityAsync();
        var worker = Guid.NewGuid();
        var open = Open() with { RequestKey = "open:award-2", HoldId = "award-2" };
        (await _store.CommitLocalAsync(open, authority)).Should().NotBeNull();
        var window = DateTimeOffset.UtcNow.AddDays(90);
        var continuation = Continuation(open, worker, window);

        var first = await _store.CommitLocalAsync(continuation, authority);

        first.Should().NotBeNull();
        (await _store.GetHoldAsync(HoldCommandIdentity.Hold(open)))!.State.Should().Be("ReleasedIntoHold");
        var child = (await _store.GetHoldAsync(HoldCommandIdentity.Hold(open, "award-2-window")))!;
        child.State.Should().Be("Held");
        child.ReleaseNotBefore.Should().Be(window);
        (await _store.CommitLocalAsync(continuation, authority))!.OutcomeJson.Should().Be(first!.OutcomeJson);
        (await _store.CommitLocalAsync(Continuation(open, worker, window.AddTicks(1)), authority)).Should().BeNull(
            "a retry that recomputed the window is a different command under a completed key");
        (await Counters()).Held.Should().Be(400);
    }

    [Fact]
    public async Task ForeignActorCannotContinueATenantKeyHold()
    {
        var authority = await PlainTenantKeyAuthorityAsync();
        var open = Open() with { RequestKey = "open:award-3", HoldId = "award-3" };
        (await _store.CommitLocalAsync(open, authority)).Should().NotBeNull();
        var foreign = Guid.NewGuid();
        await Save(new User { Id = foreign.ToString("N"), Username = foreign.ToString("N"),
            Email = foreign + "@test.invalid", PasswordHash = "test", IsActive = true });

        var hijack = Continuation(open, foreign, DateTimeOffset.UtcNow.AddDays(90)) with { ActorUserId = foreign };

        (await _store.CommitLocalAsync(hijack, await PlainTenantKeyAuthorityAsync(foreign))).Should().BeNull();
        (await _store.GetHoldAsync(HoldCommandIdentity.Hold(open)))!.State.Should().Be("Held");
        (await _store.GetHoldAsync(HoldCommandIdentity.Hold(open, "award-3-window"))).Should().BeNull();
    }

    private static HoldCommandIntent Continuation(HoldCommandIntent open, Guid worker, DateTimeOffset window)
        => open with { RequestKey = $"release-into-hold:{open.HoldId}:{open.HoldId}-window", Kind = "ReleaseIntoHold",
            Definition = null, NextHoldId = open.HoldId + "-window", NextBeneficiaryUserId = worker,
            NextReleaseNotBefore = window, NextAutoRelease = false };

    private async Task<HoldCommandAuthority> PlainTenantKeyAuthorityAsync(Guid? owner = null)
    {
        var user = owner ?? _actor;
        var apiKey = Guid.NewGuid();
        var keyHash = apiKey.ToString("N") + apiKey.ToString("N");
        string[] scopes = [AzoaScopes.TransferSign];
        await Save(new ApiKey { Id = apiKey.ToString("N"), UserId = $"user:{user:N}", IsActive = true,
            KeyHash = keyHash, KeyPrefix = "fixture", CreatedDate = DateTimeOffset.UtcNow, Scopes = [.. scopes] });
        var credential = new SigningCredentialWitness(1, "ApiKey", user, null, null, null, null, apiKey,
            keyHash, scopes, false, scopes);
        return new(1, Direct: new(1, credential, [AzoaScopes.TransferSign], []));
    }

    private async Task SaveReserveAsync(string assetId, string? tenantId)
        => await Save(new PlatformReserveLedger { Id = PlatformReserveLedger.RecordIdFor("Algorand", "Devnet", assetId),
            Chain = "Algorand", Network = "Devnet", ProtocolIdentity = "genesis", AssetId = assetId, ReserveAddress = "reserve",
            UnitName = "UNIT", TotalSupply = "1000", TenantId = tenantId,
            Partitions = ReservePartitionCodec.Encode(new Dictionary<string, ReservePartitionCounters>
                { ["contributors"] = ReservePartitionCounters.Declaring(1000) }) });

    private async Task<ReservePartitionCounters> CountersFor(string assetId)
    {
        var row = await _executor.QuerySingleAsync<PlatformReserveLedger>(
            SurrealQuery<PlatformReserveLedger>.Key(PlatformReserveLedger.RecordIdFor("Algorand", "Devnet", assetId)));
        return ReservePartitionCodec.Decode(row!.Partitions, out _)!["contributors"];
    }
}
