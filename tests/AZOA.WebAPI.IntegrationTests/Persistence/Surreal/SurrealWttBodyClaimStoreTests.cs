using System.Buffers.Binary;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using SurrealForge.Client;
using SurrealForge.Client.Connection;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealWttBodyClaimStoreTests : IAsyncLifetime
{
    private readonly string space = $"test{Guid.NewGuid():N}";
    private ISurrealConnection connection = null!;
    private SurrealWttBodyClaimStore store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(space, "wtt_body_claim");
        connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = space, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        store = new(new DefaultSurrealExecutor(connection));
    }

    public async Task DisposeAsync()
    {
        connection?.Dispose();
        await SurrealTestSchema.DropAsync(space);
    }

    [Fact]
    public async Task DifferentOperationsCannotOwnTheSameBodyAndLostResponseKeepsWinner()
    {
        var first = Candidate("operation-a"); var other = Candidate("operation-b");
        var winners = await Task.WhenAll(store.ClaimAsync(first), store.ClaimAsync(other));
        var winner = Assert.Single(winners.Where(value => value is not null))!;
        Assert.Equal(winner.OperationId, (await store.GetAsync(winner.Id))!.OperationId);
        Assert.NotNull(await store.ClaimAsync(winner));
        Assert.Null(await store.ClaimAsync(winner.OperationId == first.OperationId ? other : first));
    }

    [Fact]
    public async Task ConflictingBodyForPublicationTupleIsRejectedAndOtherDeploymentIsIndependent()
    {
        var first = Candidate("operation-a");
        Assert.NotNull(await store.ClaimAsync(first));
        var changed = Candidate("operation-a", payloadMarker: 2);
        Assert.NotEqual(first.Id, changed.Id);
        Assert.Null(await store.ClaimAsync(changed));
        var otherDeployment = Candidate("operation-a", targetTokenBridge: "another-token-bridge");
        Assert.NotNull(await store.ClaimAsync(otherDeployment));
        var alteredActor = Candidate("operation-a"); alteredActor.ActorUserId = "other-actor";
        Assert.Null(await store.ClaimAsync(alteredActor));
        var displayAlias = Candidate("operation-b"); displayAlias.TargetTokenBridge = "TOKEN-BRIDGE";
        Assert.Equal(first.TargetPartition, displayAlias.TargetPartition);
        Assert.Null(await store.ClaimAsync(displayAlias));
        var alteredSource = Candidate("operation-a"); alteredSource.SourceProtocolIdentity = "other-source-genesis";
        Assert.Null(await store.ClaimAsync(alteredSource));
    }

    [Fact]
    public async Task ForgedDigestOrTupleCannotReserveCanonicalIdentity()
    {
        var candidate = Candidate("operation-a"); candidate.Sequence = "1";
        await Assert.ThrowsAsync<ArgumentException>(() => store.ClaimAsync(candidate));
        candidate = Candidate("operation-a"); candidate.BodyHex = null!;
        await Assert.ThrowsAsync<ArgumentException>(() => store.ClaimAsync(candidate));
        candidate = Candidate("operation-a"); candidate.TargetTokenBridgeHex = candidate.TargetTokenBridgeHex.ToUpperInvariant();
        await Assert.ThrowsAsync<ArgumentException>(() => store.ClaimAsync(candidate));
        candidate = Candidate("operation-a"); candidate.BodyDigest = new string('a', 64);
        candidate.Id = WttBodyClaimIdentity.Id(candidate.TargetPartition, candidate.BodyDigest);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ClaimAsync(candidate));
    }

    private static WttBodyClaim Candidate(string operation, byte payloadMarker = 1, string targetTokenBridge = "token-bridge")
    {
        var body = new byte[52]; body[51] = payloadMarker;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8), 8);
        body.AsSpan(10, 32).Fill(0x42);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(42), ulong.MaxValue);
        var tokenHex = new string(targetTokenBridge == "token-bridge" ? 'c' : 'd', 64);
        var partition = WttBodyClaimIdentity.Partition(new(2, new string('a', 64), new string('b', 64), tokenHex));
        var digest = Convert.ToHexStringLower(Keccak256.ComputeHash(Keccak256.ComputeHash(body)));
        return new() { Id = WttBodyClaimIdentity.Id(partition, digest), TargetPartition = partition,
            TargetProtocolChain = 2, TargetGenesisHex = new string('a', 64), TargetCoreBridgeHex = new string('b', 64), TargetTokenBridgeHex = tokenHex,
            TargetProtocolIdentity = "target-genesis", TargetCoreBridge = "core", TargetTokenBridge = targetTokenBridge,
            SourceProtocolIdentity = "source-genesis", EmitterChain = 8, EmitterAddress = Convert.ToHexStringLower(body.AsSpan(10, 32)),
            Sequence = "18446744073709551615", BodyHex = Convert.ToHexStringLower(body), BodyDigest = digest,
            OperationId = operation, IntentHash = "frozen-intent", ActorUserId = "actor", CreatedAt = DateTimeOffset.UtcNow };
    }
}
