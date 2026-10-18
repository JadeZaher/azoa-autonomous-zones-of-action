using System.Text.Json;
using AZOA.WebAPI.Helpers;
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
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<SwapOperationRecord>("first").Where(seed => true).Set(row => row.State, "Cancelled").Build())).EnsureAllOk();
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

    [Fact]
    public async Task ContendingClaimWaitsOnAPendingHolderThenRecoversTheLostEagerReleaseOnceTheSameStageConfirms()
    {
        await SeedOperation("first"); await SeedOperation("second");
        var first = Candidate("first");
        (await _store.ClaimAsync(first)).Should().NotBeNull();
        var proof = new EthereumDexAllowanceReleaseEvidence(1, first.ProtocolIdentity, first.OwnerAddress, first.TokenAddress,
            first.ExecutorAddress, "0", "0x" + new string('d', 64), "0x" + new string('e', 64), "100", "Swap");
        string Observation(string verdict) => JsonSerializer.Serialize(new SwapStageObservation(verdict, [proof.TransactionId], "eip155:31337",
            "1010", "2000", "10", "21000", "0", JsonSerializer.Serialize(new { AllowanceRelease = proof })));
        await Save(new SwapStageRecord
        {
            Id = "settled", OperationId = "first", IntentHash = first.IntentHash, Stage = "Swap", State = "Pending",
            PreparedJson = "{}", AuthorizedAt = DateTimeOffset.UtcNow, ObservationJson = Observation("Pending"),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<SwapOperationRecord>("first").Where(seed => true)
            .Set(row => row.CurrentStageId, "settled").Set(row => row.State, "Preparing").Build())).EnsureAllOk();

        (await _store.ClaimAsync(Candidate("second"))).Should().BeNull("a pending holder keeps the allowance lane");
        (await _store.GetAsync(first.Id))!.OperationId.Should().Be("first");

        // The holder's eager release is lost (never called); only the contending claim can recover it from the saved proof.
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<SwapStageRecord>("settled").Where(seed => true)
            .Set(row => row.State, "Confirmed").Set(row => row.ObservationJson, Observation("Confirmed")).Build())).EnsureAllOk();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<SwapOperationRecord>("first").Where(seed => true)
            .Set(row => row.State, "Completed").Build())).EnsureAllOk();

        var next = await _store.ClaimAsync(Candidate("second"));

        next.Should().NotBeNull("a lost eager release is recovered from the holder's own saved proof, never from absence");
        var row = (await _store.GetAsync(first.Id))!;
        row.OperationId.Should().Be("second");
        row.State.Should().Be("Held");
    }

    [Fact]
    public async Task ConcurrentReleasesWithTheSameStageProofBothReportTheSingleRelease()
    {
        await SeedOperation("first");
        var first = Candidate("first");
        (await _store.ClaimAsync(first)).Should().NotBeNull();
        var proof = new EthereumDexAllowanceReleaseEvidence(1, first.ProtocolIdentity, first.OwnerAddress, first.TokenAddress,
            first.ExecutorAddress, "0", "0x" + new string('d', 64), "0x" + new string('e', 64), "100", "Swap");
        await Save(new SwapStageRecord
        {
            Id = "settled", OperationId = "first", IntentHash = first.IntentHash, Stage = "Swap", State = "Confirmed",
            PreparedJson = "{}", AuthorizedAt = DateTimeOffset.UtcNow,
            ObservationJson = JsonSerializer.Serialize(new SwapStageObservation("Confirmed", [proof.TransactionId], "eip155:31337",
                "1010", "2000", "10", "21000", "0", JsonSerializer.Serialize(new { AllowanceRelease = proof }))),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });

        var released = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _store.ReleaseAsync(first.Id, "first", "settled")));

        released.Should().AllBeEquivalentTo(true, "a loser re-reads and recognises the winner's identical release");
        var row = (await _store.GetAsync(first.Id))!;
        row.State.Should().Be("Released");
        row.ReleaseStageId.Should().Be("settled");
    }

    [Fact]
    public async Task A_same_call_winner_holds_the_lane_until_an_operator_settles_it_then_a_new_swap_claims_it()
    {
        var swaps = new SurrealSwapOperationStore(_executor);
        var intent = SameCallIntent("first");
        var intentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(intent));
        var intentHash = CanonicalJson.HashCanonical(intentJson);
        var plan = new SwapStagePlan("Swap", 0, "same-call-preparation", intentHash, [intent.Terms.Owner.Address], "\"unsigned\"", "{}", "100000", "0");
        var planJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan));
        var first = Candidate("first");
        first.IntentHash = intentHash;
        var winner = "0x" + new string('d', 64);
        var review = new SwapStageObservation("NeedsOperator", [], intent.Terms.Network.ProtocolIdentity,
            EvidenceJson: "{\"Format\":\"ethereum-nonce-winner-same-call/1\"}", Detail: "same call, wallet-chosen envelope");
        var reviewJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(review));
        await Save(new SwapOperationRecord
        {
            Id = "first", IntentJson = intentJson, IntentHash = intentHash, State = "NeedsOperator", CostAccountingVersion = 1,
            CurrentStageId = "same-call", Version = 4, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow,
        });
        await Save(new SwapStageRecord
        {
            Id = "same-call", OperationId = "first", IntentHash = intentHash, Stage = "Swap", PlanJson = planJson,
            PlanHash = CanonicalJson.HashCanonical(planJson), State = "NeedsOperator", ExternalExposedAt = DateTimeOffset.UtcNow,
            ObservationJson = reviewJson, Version = 2, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await SeedOperation("second");
        (await _store.ClaimAsync(first)).Should().NotBeNull();
        (await _store.ClaimAsync(Candidate("second"))).Should().BeNull("a same-call winner may have spent the allowance; the lane stays held");

        var audit = new SwapOperatorSettlement(1, Guid.NewGuid(), "Wallet re-priced the disclosed swap", DateTimeOffset.UtcNow,
            CanonicalJson.HashCanonical(reviewJson));
        var settled = new SwapStageObservation("Confirmed", [winner], intent.Terms.Network.ProtocolIdentity, intent.Terms.GrossInput, "1995",
            intent.Terms.PlatformFee.Amount, "21000000000000", "0", JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["OperatorSettlement"] = audit,
                ["AllowanceRelease"] = new EthereumDexAllowanceReleaseEvidence(1, first.ProtocolIdentity, first.OwnerAddress, first.TokenAddress,
                    first.ExecutorAddress, "0", winner, "0x" + new string('e', 64), "15", "Swap"),
            }));
        (await swaps.SettleOperatorReviewAsync("first", 4, "same-call", 2, settled with { TransactionIds = [] })).Should().BeNull();
        var forged = audit with { ReplacedObservationHash = new string('0', 64) };
        (await swaps.SettleOperatorReviewAsync("first", 4, "same-call", 2, settled with
        {
            EvidenceJson = settled.EvidenceJson!.Replace(audit.ReplacedObservationHash, forged.ReplacedObservationHash),
        })).Should().BeNull("the audit must bind the exact review it replaces");

        var recorded = await swaps.SettleOperatorReviewAsync("first", 4, "same-call", 2, settled);

        recorded!.State.Should().Be("Confirmed");
        (await swaps.SettleOperatorReviewAsync("first", 4, "same-call", 2, settled))!.State.Should().Be("Confirmed", "an exact replay is idempotent");
        var operation = (await swaps.GetAsync("first"))!;
        operation.State.Should().Be("StageResolved");
        operation.ConfirmedEffectNetworkFee.Should().Be("21000000000000", "the winner's actual fee is charged, whatever the wallet chose");
        var next = await _store.ClaimAsync(Candidate("second"));
        next.Should().NotBeNull("the settled winner's zero-allowance evidence releases the lane to the next swap");
        (await _store.GetAsync(first.Id))!.OperationId.Should().Be("second");
    }

    private static SwapIntent SameCallIntent(string operationId)
    {
        var owner = new DexParticipant(Guid.NewGuid(), Guid.NewGuid(), "0x" + new string('a', 40));
        var terms = new DexQuoteTerms(1, owner.UserId, null, owner, owner, owner.Address, new("Ethereum", "Devnet", "eip155:31337"),
            new("0x" + new string('b', 40), "IN", 18, false), new("0x" + new string('c', 40), "OUT", 18, false), "1010", "1000", "2000", "1990", 50,
            new("input-base-units-v1", 1, "10", 0, "10", "0x" + new string('9', 40)), new("100000", "0", "100000"), "UniswapV3",
            "classic-exact-input/1", JsonSerializer.SerializeToElement(new { fixture = true }), DateTimeOffset.UtcNow, []);
        return new(1, operationId, "quote", new string('f', 64), terms, "request");
    }

    private Task SeedOperation(string id) => Save(new SwapOperationRecord
    {
        Id = id, IntentHash = new string('a', 64), State = "Admitted",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow
    });

    private async Task Save<T>(T row) where T : class, ISurrealRecord, new()
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
