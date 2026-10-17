using System.Text.Json;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealEthereumDexAllowanceTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealEthereumDexAllowanceStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "ethereum_dex_allowance", "swap_operation", "swap_stage");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password
        });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new(_executor);
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task ConcurrentOperationsCannotShareAllowanceButPhysicalNetworksAreIndependent()
    {
        await SeedOperation("first"); await SeedOperation("second");
        var first = Candidate("first"); var second = Candidate("second");
        var claims = await Task.WhenAll(_store.ClaimAsync(first), _store.ClaimAsync(second));
        claims.Count(row => row is not null).Should().Be(1);
        var winner = claims.Single(row => row is not null)!;
        (await _store.ClaimAsync(Candidate(winner.OperationId)))!.OperationId.Should().Be(winner.OperationId);
        var otherNetwork = Candidate("second", "eip155:31338:" + new string('b', 64));
        (await _store.ClaimAsync(otherNetwork)).Should().NotBeNull();
        (await _store.GetAsync(first.Id))!.State.Should().Be("Held");
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task CanceledOwnerIsReplaceableOnlyWhenNoHistoricalStageWasEverExposed(bool authorized, bool exposed, bool replaceable)
    {
        await SeedOperation("first"); await SeedOperation("second");
        var first = Candidate("first");
        (await _store.ClaimAsync(first)).Should().NotBeNull();
        await Save(new SwapStageRecord
        {
            Id = "old-stage", OperationId = "first", IntentHash = first.IntentHash, Stage = "ApproveExact",
            State = "Cancelled", AuthorizedAt = authorized ? DateTimeOffset.UtcNow : null,
            ExternalExposedAt = exposed ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<SwapOperationRecord>("first").Set(row => row.State, "Cancelled").Build())).EnsureAllOk();
        var next = await _store.ClaimAsync(Candidate("second"));
        (next is not null).Should().Be(replaceable);
        (await _store.GetAsync(first.Id))!.OperationId.Should().Be(replaceable ? "second" : "first");
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("genesis")]
    public async Task NoncanonicalResourceCannotCreateParallelAllowanceOwnership(string changed)
    {
        await SeedOperation("first");
        var candidate = Candidate("first");
        if (changed == "owner") candidate.OwnerAddress = candidate.OwnerAddress.ToUpperInvariant();
        else candidate.ProtocolIdentity = "eip155:31337";
        (await _store.ClaimAsync(candidate)).Should().BeNull();
        (await _store.GetAsync(candidate.Id)).Should().BeNull();
    }

    [Theory]
    [InlineData("ApproveExact", "Confirmed", false)]
    [InlineData("Swap", "Pending", false)]
    [InlineData("Swap", "Confirmed", true)]
    [InlineData("RevokeAllowance", "Confirmed", true)]
    public async Task OnlyConfirmedTerminalEffectReleasesOriginalOwner(string stageName, string verdict, bool releases)
    {
        await SeedOperation("first"); await SeedOperation("second");
        var first = Candidate("first");
        await _store.ClaimAsync(first);
        await Save(new SwapStageRecord
        {
            Id = "proof", OperationId = "first", IntentHash = first.IntentHash, Stage = stageName,
            State = verdict, PreparedJson = "{}", AuthorizedAt = DateTimeOffset.UtcNow,
            ObservationJson = JsonSerializer.Serialize(new SwapStageObservation(verdict, ["0x" + new string('d', 64)], "eip155:31337",
                "1010", "2000", "10", "21000", "0", JsonSerializer.Serialize(new
                {
                    AllowanceRelease = new EthereumDexAllowanceReleaseEvidence(1, first.ProtocolIdentity, first.OwnerAddress,
                        first.TokenAddress, first.ExecutorAddress, "0", "0x" + new string('d', 64), "0x" + new string('e', 64), "100", stageName)
                }))),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        (await _store.ReleaseAsync(first.Id, "second", "proof")).Should().BeFalse();
        (await _store.ReleaseAsync(first.Id, "first", "proof")).Should().Be(releases);
        (await _store.ReleaseAsync(first.Id, "first", "proof")).Should().Be(releases);
        var next = await _store.ClaimAsync(Candidate("second"));
        (next is not null).Should().Be(releases);
        if (releases)
        {
            (await _store.ClaimAsync(first)).Should().BeNull();
            (await _store.ReleaseAsync(first.Id, "first", "proof")).Should().BeFalse();
            next!.ReleaseEvidenceJson.Should().BeNull();
            next.ReleaseStageId.Should().BeNull();
        }
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("token")]
    [InlineData("executor")]
    [InlineData("allowance")]
    public async Task ConfirmedReceiptForAnotherResourceOrResidualAllowanceDoesNotRelease(string changed)
    {
        await SeedOperation("first");
        var candidate = Candidate("first");
        await _store.ClaimAsync(candidate);
        var proof = new EthereumDexAllowanceReleaseEvidence(1, candidate.ProtocolIdentity, candidate.OwnerAddress,
            candidate.TokenAddress, candidate.ExecutorAddress, "0", "0x" + new string('d', 64), "0x" + new string('e', 64), "100", "Swap");
        proof = changed switch
        {
            "owner" => proof with { Owner = "0x" + new string('f', 40) },
            "token" => proof with { Token = "0x" + new string('f', 40) },
            "executor" => proof with { Executor = "0x" + new string('f', 40) },
            _ => proof with { AllowanceAfter = "1" }
        };
        await Save(new SwapStageRecord
        {
            Id = "wrong-proof", OperationId = "first", IntentHash = candidate.IntentHash, Stage = "Swap", State = "Confirmed",
            PreparedJson = "{}", AuthorizedAt = DateTimeOffset.UtcNow,
            ObservationJson = JsonSerializer.Serialize(new SwapStageObservation("Confirmed", [proof.TransactionId], "eip155:31337",
                "1010", "2000", "10", "21000", "0", JsonSerializer.Serialize(new { AllowanceRelease = proof }))),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        (await _store.ReleaseAsync(candidate.Id, "first", "wrong-proof")).Should().BeFalse();
        (await _store.GetAsync(candidate.Id))!.State.Should().Be("Held");
    }

    private Task SeedOperation(string id) => Save(new SwapOperationRecord
    {
        Id = id, IntentHash = new string('a', 64), State = "Admitted",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow
    });

    private async Task Save<T>(T row) where T : class, ISurrealRecord
        => (await _executor.ExecuteAsync(SurrealWriter.Create(row))).EnsureAllOk();

    private static EthereumDexAllowance Candidate(string operation, string? protocol = null)
    {
        protocol ??= "eip155:31337:" + new string('a', 64);
        var owner = "0x" + new string('a', 40); var token = "0x" + new string('b', 40); var executor = "0x" + new string('c', 40);
        return new()
        {
            Id = EthereumDexAllowanceIdentity.Create(protocol, owner, token, executor), ProtocolIdentity = protocol,
            OwnerAddress = owner, TokenAddress = token, ExecutorAddress = executor,
            OperationId = operation, IntentHash = new string('a', 64)
        };
    }
}
