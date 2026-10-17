using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealEthereumPreparationStoreTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private SurrealEthereumPreparationStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "ethereum_preparation", "ethereum_signer_checkpoint");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        _store = new SurrealEthereumPreparationStore(new DefaultSurrealExecutor(_connection));
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task Competing_calls_cannot_own_the_same_signer_nonce_and_stale_writers_cannot_replace_wire()
    {
        var first = Candidate("first", 7);
        var second = Candidate("second", 7);
        var claims = await Task.WhenAll(_store.TryClaimAsync(first), _store.TryClaimAsync(second));
        claims.Count(row => row is not null).Should().Be(1);
        var winner = claims.Single(row => row is not null)!;
        winner.SignedTransactionBase64 = "AQID";
        winner.TransactionId = "durable-hash";
        winner.State = "Prepared";
        var stored = await _store.UpdateAsync(winner, 0);
        stored!.Version.Should().Be(1);
        winner.SignedTransactionBase64 = "BAUG";
        (await _store.UpdateAsync(winner, 0)).Should().BeNull();
        (await _store.GetAsync(winner.Id))!.SignedTransactionBase64.Should().Be("AQID");
        await _store.TryClaimAsync(Candidate("next", 12));
        (await _store.GetHighestNonceAsync(first.ProtocolIdentity, first.Sender))!.Nonce.Should().Be(12.ToString("D78"));
        (await _store.ListAuthorizedAsync("Devnet")).Should().BeEmpty();
        stored.State = "SubmissionAuthorized";
        await _store.UpdateAsync(stored, stored.Version);
        (await _store.ListAuthorizedAsync("Devnet")).Should().ContainSingle();
        (await _store.ListAuthorizedAsync("Mainnet")).Should().BeEmpty();
    }

    [Fact]
    public async Task Checkpoint_is_create_only_and_another_epoch_cannot_relabel_it()
    {
        var checkpoint = new EthereumSignerCheckpoint
        {
            Id = "signer", ProtocolIdentity = "eip155:31337", Sender = "sender", ActivationEpoch = "first",
            InitialNonce = "7", BlockHash = "finalized-block", ActorUserId = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow,
        };
        await _store.ClaimCheckpointAsync(checkpoint);
        checkpoint.ActivationEpoch = "second";
        checkpoint.InitialNonce = "0";
        var retained = await _store.ClaimCheckpointAsync(checkpoint);
        retained.ActivationEpoch.Should().Be("first");
        retained.InitialNonce.Should().Be("7");
    }

    [Fact]
    public async Task Refilling_a_released_nonce_keeps_the_released_key_locked_by_a_tombstone()
    {
        var released = (await _store.TryClaimAsync(Candidate("released", 9)))!;
        released.State = "Released";
        released = (await _store.UpdateAsync(released, released.Version))!;
        (await _store.ListReleasedAsync(released.ProtocolIdentity, released.Sender, 16)).Should().ContainSingle();

        var successor = await _store.TryClaimReleasedAsync(released, Candidate("successor", 9));

        successor!.Nonce.Should().Be(9.ToString("D78"));
        var tombstone = (await _store.GetAsync("released"))!;
        tombstone.State.Should().Be("ReleasedRefilled", "the released key must never reserve a fresh nonce again");
        tombstone.NonceSlot.Should().Be("released");
        tombstone.Nonce.Should().Be(9.ToString("D78"), "the tombstone keeps its original nonce as evidence");
        (await _store.ListReleasedAsync(released.ProtocolIdentity, released.Sender, 16)).Should().BeEmpty();
        (await _store.TryClaimAsync(Candidate("intruder", 9))).Should().BeNull("the successor is the one live owner of the nonce");
        (await _store.TryClaimReleasedAsync(released, Candidate("second-successor", 9))).Should().BeNull("a stale released row cannot be refilled twice");
        (await _store.GetHighestNonceAsync(released.ProtocolIdentity, released.Sender))!.Nonce.Should().Be(9.ToString("D78"));
    }

    private static EthereumPreparation Candidate(string id, int nonce) => new()
    {
        Id = id, ProtocolIdentity = "eip155:31337", Sender = "sender", Network = "Devnet", Nonce = nonce.ToString("D78"),
        PreparationKey = id, CallFingerprint = id, IntentJson = "{}", OwnerUserId = Guid.Empty.ToString("N"),
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };
}
