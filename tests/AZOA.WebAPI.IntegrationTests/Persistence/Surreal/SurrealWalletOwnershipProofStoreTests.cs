using AZOA.WebAPI.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Real-DB round trip of the audit-1 M2 wallet ownership proof: canonical identity, one current prover per address.</summary>
public sealed class SurrealWalletOwnershipProofStoreTests : IAsyncLifetime
{
    private const string Lower = "0xabcdefabcdefabcdefabcdefabcdefabcdefabcd";
    private static readonly string Upper = Lower.ToUpperInvariant().Replace("0X", "0x");
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private SurrealWalletOwnershipProofStore _proofs = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "wallet_ownership_proof");
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
    public async Task Proof_is_read_back_under_any_casing_and_a_newer_verified_proof_supersedes_the_prover()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        (await _proofs.GetAsync("Ethereum", Upper)).Should().BeNull();

        await _proofs.RecordAsync(new WalletOwnershipProof(first, Guid.NewGuid(), "Ethereum", Upper, DateTime.UtcNow));
        var read = await _proofs.GetAsync("ethereum", Lower);
        read!.UserId.Should().Be(first);
        read.Address.Should().Be(Lower);
        read.ChainType.Should().Be("ethereum");

        await _proofs.RecordAsync(new WalletOwnershipProof(second, Guid.NewGuid(), "ethereum", Lower, DateTime.UtcNow));
        (await _proofs.GetAsync("Ethereum", Upper))!.UserId.Should().Be(second, "one current prover per canonical address");
        (await _proofs.GetAsync("ethereum", "not-an-address")).Should().BeNull();
    }
}
