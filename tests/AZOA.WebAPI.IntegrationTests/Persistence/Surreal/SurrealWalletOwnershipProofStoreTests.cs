using AZOA.WebAPI.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using WalletRow = AZOA.WebAPI.Persistence.SurrealDb.Models.Wallet;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Real-DB round trip of the audit-1 M2 wallet ownership proof: canonical identity, one current prover per address,
/// and the RV-L4-03 supersession rule (a still-valid holder is never replaced; a stale one is).</summary>
public sealed class SurrealWalletOwnershipProofStoreTests : IAsyncLifetime
{
    private const string Lower = "0xabcdefabcdefabcdefabcdefabcdefabcdefabcd";
    private static readonly string Upper = Lower.ToUpperInvariant().Replace("0X", "0x");
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private SurrealWalletOwnershipProofStore _proofs = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "wallet", "wallet_ownership_proof");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        _proofs = new(new DefaultSurrealExecutor(_connection));
    }

    public async Task DisposeAsync()
    {
        _connection.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task Proof_is_read_back_under_any_casing_and_a_stale_holder_is_superseded()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        (await _proofs.GetAsync("Ethereum", Upper)).Should().BeNull();

        (await _proofs.RecordAsync(new WalletOwnershipProof(first, Guid.NewGuid(), "Ethereum", Upper, DateTime.UtcNow))).IsError.Should().BeFalse();
        var read = await _proofs.GetAsync("ethereum", Lower);
        read!.UserId.Should().Be(first);
        read.Address.Should().Be(Lower);
        read.ChainType.Should().Be("ethereum");

        var superseded = await _proofs.RecordAsync(new WalletOwnershipProof(second, Guid.NewGuid(), "ethereum", Lower, DateTime.UtcNow));
        superseded.Result!.SupersededUserId.Should().Be(first, "the first proof names no wallet the first user still holds");
        (await _proofs.GetAsync("Ethereum", Upper))!.UserId.Should().Be(second, "one current prover per canonical address");
        (await _proofs.GetAsync("ethereum", "not-an-address")).Should().BeNull();
    }

    [Fact]
    public async Task A_holder_whose_wallet_still_holds_the_address_is_never_replaced_until_it_removes_the_wallet()
    {
        var alice = Guid.NewGuid();
        var mallory = Guid.NewGuid();
        var aliceWallet = Guid.NewGuid();
        await SaveExternalWalletAsync(aliceWallet, alice);
        await _proofs.RecordAsync(new WalletOwnershipProof(alice, aliceWallet, "ethereum", Lower, DateTime.UtcNow));

        var refused = await _proofs.RecordAsync(new WalletOwnershipProof(mallory, Guid.NewGuid(), "ethereum", Lower, DateTime.UtcNow));
        (await _proofs.IsSignatureProvenAsync(aliceWallet, alice)).Should().BeTrue();
        var executor = new DefaultSurrealExecutor(_connection);
        (await executor.ExecuteAsync(SurrealWriter.DeleteOnly<WalletRow>(aliceWallet.ToString("N")).Build())).EnsureAllOk();
        var moved = await _proofs.RecordAsync(new WalletOwnershipProof(mallory, Guid.NewGuid(), "ethereum", Lower, DateTime.UtcNow));

        refused.IsError.Should().BeTrue();
        refused.Message.Should().Be(SurrealWalletOwnershipProofStore.HeldByAnotherUser);
        moved.Result!.SupersededUserId.Should().Be(alice);
        (await _proofs.GetAsync("ethereum", Lower))!.UserId.Should().Be(mallory);
    }

    /// <summary>RV-L4-09: a stale foreign proof (holder deleted the wallet) never blocks the new key holder from adding the address.</summary>
    [Fact]
    public async Task Only_a_still_valid_foreign_proof_blocks_adding_the_address()
    {
        var alice = Guid.NewGuid();
        var carol = Guid.NewGuid();
        var aliceWallet = Guid.NewGuid();
        await SaveExternalWalletAsync(aliceWallet, alice);
        await _proofs.RecordAsync(new WalletOwnershipProof(alice, aliceWallet, "ethereum", Lower, DateTime.UtcNow));

        var whileHeld = await _proofs.IsHeldByAnotherUserAsync("Ethereum", Upper, carol);
        var forHolder = await _proofs.IsHeldByAnotherUserAsync("ethereum", Lower, alice);
        (await new DefaultSurrealExecutor(_connection).ExecuteAsync(SurrealWriter.DeleteOnly<WalletRow>(aliceWallet.ToString("N")).Build())).EnsureAllOk();
        var afterDelete = await _proofs.IsHeldByAnotherUserAsync("ethereum", Lower, carol);

        whileHeld.Should().BeTrue();
        forHolder.Should().BeFalse("the holder's own proof never blocks them");
        afterDelete.Should().BeFalse("the proof is stale once its holder no longer has the wallet");
        (await _proofs.GetAsync("ethereum", Lower))!.UserId.Should().Be(alice, "the stale row stays until the new holder's proof supersedes it");
    }

    private async Task SaveExternalWalletAsync(Guid walletId, Guid owner)
    {
        var executor = new DefaultSurrealExecutor(_connection);
        (await executor.ExecuteAsync(SurrealWriter.Create(new WalletRow
        {
            Id = walletId.ToString("N"), UserId = SurrealLink.ToLink("user", owner.ToString("N"))!, ChainType = "ethereum",
            Address = Upper, WalletType = WalletRow.WalletTypeKind.External, CreatedDate = DateTimeOffset.UtcNow,
        }))).EnsureAllOk();
    }
}
