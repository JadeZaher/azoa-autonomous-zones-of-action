
using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Admin;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Services.Reserve;

public class PlatformTokenGenesisTests
{
    private const string Chain = "Algorand";
    private const string ReserveAddress = "RESERVEADDRESSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string AssetId = "7001";

    private static readonly Guid OperatorUserId = NodeOperatorIdentity.UserId;

    [Fact]
    public async Task RunAsync_admits_pending_creation_without_claiming_an_asset()
    {
        var harness = GenesisHarness.Create();

        var outcome = await harness.RunAsync();

        outcome.IsError.Should().BeFalse(outcome.Message);
        outcome.Result!.Created.Should().BeFalse();
        outcome.Result.Operation!.State.Should().Be("AwaitingConsent");
        outcome.Result.Definition.AssetId.Should().BeEmpty();
        outcome.Result.Definition.UnitName.Should().Be(PlatformTokenGenesisDefaults.UnitName);
        outcome.Result.Definition.Decimals.Should().Be(PlatformTokenGenesisDefaults.Decimals);

        harness.Minter.MintCount.Should().Be(1);
        var row = harness.Store.Single();
        row.Status.Should().Be(PlatformToken.StatusPending);
        row.AssetId.Should().BeNull();
        row.ReserveAddress.Should().Be(ReserveAddress);
        row.GenesisUserId.Should().Be(OperatorUserId.ToString("N"));
        row.ActivatedAt.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_defaults_to_the_N_X2_definition_when_the_operator_supplies_none()
    {
        var harness = GenesisHarness.Create();

        await harness.RunAsync(new PlatformTokenGenesisRequest { ConfirmIrreversible = true });

        var minted = harness.Minter.LastRequest!;
        minted.ChainType.Should().Be(PlatformTokenGenesisDefaults.ChainType);
        minted.UnitName.Should().Be(PlatformTokenGenesisDefaults.UnitName);
        minted.Decimals.Should().Be(PlatformTokenGenesisDefaults.Decimals);
        minted.Total.Should().Be(PlatformTokenGenesisDefaults.TotalBaseUnits);
    }

    [Fact]
    public async Task RunAsync_replays_a_completed_genesis_without_minting_again()
    {
        var harness = GenesisHarness.Create();
        await harness.RunAsync();
        harness.Store.SeedVerifiedReadProjection();

        var second = await harness.RunAsync();

        second.IsError.Should().BeFalse(second.Message);
        second.Result!.Created.Should().BeFalse("the asset already exists; nothing new was created");
        second.Result.Definition.AssetId.Should().Be(AssetId);
        harness.Minter.MintCount.Should().Be(1, "a replay must never broadcast a second create");
        harness.Store.Rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task RunAsync_refuses_a_second_genesis_that_changes_the_definition()
    {
        var harness = GenesisHarness.Create();
        await harness.RunAsync();
        harness.Store.SeedVerifiedReadProjection();

        var second = await harness.RunAsync(Request(total: 999_000_000));

        second.IsError.Should().BeTrue();
        second.Code.Should().Be(PlatformTokenGenesisCodes.AlreadyComplete);
        second.Message.Should().Contain("SECOND platform unit");
        harness.Minter.MintCount.Should().Be(1, "the refusal must happen before any broadcast");
    }

    [Fact]
    public async Task RunAsync_refuses_when_a_claimed_genesis_carries_a_different_definition()
    {
        var harness = GenesisHarness.Create();
        harness.Minter.FailWith("chain unreachable");
        await harness.RunAsync();

        var contradicting = await harness.RunAsync(Request(decimals: 6));

        contradicting.IsError.Should().BeTrue();
        contradicting.Code.Should().Be(PlatformTokenGenesisCodes.DefinitionFrozen);
        harness.Minter.MintCount.Should().Be(1, "the second attempt never reached the mint");
    }

    [Fact]
    public async Task RunAsync_leaves_a_failed_attempt_resumable_rather_than_terminal()
    {
        var harness = GenesisHarness.Create();
        harness.Minter.FailWith("broadcast timed out");

        var failed = await harness.RunAsync();

        failed.IsError.Should().BeTrue();
        var row = harness.Store.Single();
        row.Status.Should().Be(PlatformToken.StatusPending, "a timed-out mint may still have broadcast");
        row.LastFailureReason.Should().Contain("broadcast timed out");
        row.AssetId.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_resumes_a_claimed_genesis_under_the_same_operation_key()
    {
        var harness = GenesisHarness.Create();
        harness.Minter.FailWith("broadcast timed out");
        await harness.RunAsync();
        var firstKey = harness.Minter.LastOperationKey;

        harness.Minter.Succeed();
        var resumed = await harness.RunAsync();

        resumed.IsError.Should().BeFalse(resumed.Message);
        resumed.Result!.Created.Should().BeFalse();
        resumed.Result.Operation!.State.Should().Be("AwaitingConsent");
        harness.Minter.LastOperationKey.Should().Be(
            firstKey, "a resume must replay the original mint, not anchor a new one");
        harness.Store.Rows.Should().HaveCount(1);
        harness.Store.Single().Status.Should().Be(PlatformToken.StatusPending);
    }

    [Fact]
    public async Task RunAsync_reports_the_asset_id_when_the_mint_lands_but_the_record_does_not()
    {
        var harness = GenesisHarness.Create();
        harness.Minter.ReportCompletedWithoutProjection = true;

        var outcome = await harness.RunAsync();

        outcome.IsError.Should().BeTrue();
        outcome.Code.Should().Be(PlatformTokenGenesisCodes.Unrecorded);
        outcome.Message.Should().Contain(AssetId, "an operator cannot repair an asset id they were never told");
    }

    [Fact]
    public async Task RunAsync_refuses_without_the_irreversibility_acknowledgement()
    {
        var harness = GenesisHarness.Create();

        var outcome = await harness.RunAsync(new PlatformTokenGenesisRequest { ConfirmIrreversible = false });

        outcome.IsError.Should().BeTrue();
        outcome.Code.Should().Be(PlatformTokenGenesisCodes.NotConfirmed);
        harness.Store.Rows.Should().BeEmpty("nothing may be claimed before the acknowledgement");
        harness.Minter.MintCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_refuses_when_the_operator_asserts_a_different_network()
    {
        var harness = GenesisHarness.Create(ChainNetwork.Devnet);

        var outcome = await harness.RunAsync(Request(expectedNetwork: "Mainnet"));

        outcome.IsError.Should().BeTrue();
        outcome.Code.Should().Be(PlatformTokenGenesisCodes.NetworkMismatch);
        outcome.Message.Should().Contain("Devnet");
        harness.Minter.MintCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_accepts_the_intended_total_now_the_mint_path_carries_it()
    {
        var harness = GenesisHarness.Create();

        var outcome = await harness.RunAsync(Request(total: PlatformTokenGenesisDefaults.IntendedTotalBaseUnits));

        outcome.IsError.Should().BeFalse(outcome.Message);
        harness.Minter.MintCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_refuses_a_unit_name_longer_than_the_chain_allows()
    {
        var harness = GenesisHarness.Create();

        var outcome = await harness.RunAsync(Request(unitName: "TOOLONGUNIT"));

        outcome.IsError.Should().BeTrue();
        outcome.Message.Should().Contain("8-byte");
        harness.Minter.MintCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_fails_closed_when_the_platform_token_state_cannot_be_read()
    {
        var harness = GenesisHarness.Create();
        harness.Store.FailReads = true;

        var outcome = await harness.RunAsync();

        outcome.IsError.Should().BeTrue();
        outcome.Message.Should().Contain("could not be read");
        harness.Minter.MintCount.Should().Be(0, "minting over an unreadable state is how a second unit is created");
    }

    [Fact]
    public async Task RunAsync_fails_closed_when_no_reserve_account_resolves()
    {
        var harness = GenesisHarness.Create();
        harness.WithoutReserveAccount();

        var outcome = await harness.RunAsync();

        outcome.IsError.Should().BeTrue();
        harness.Store.Rows.Should().BeEmpty("a genesis that cannot be finished must never be claimed");
        harness.Minter.MintCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_does_not_refund_the_reserve_on_dev_network_replay()
    {
        var harness = GenesisHarness.Create(ChainNetwork.Devnet);
        harness.WithFaucet(nativeBalanceBaseUnits: "0");

        var outcome = await harness.RunAsync();

        outcome.IsError.Should().BeFalse(outcome.Message);
        harness.FaucetDispenses.Should().Be(0);
        outcome.Result!.OperationsNote.Should().Contain("faucet");
    }

    [Fact]
    public async Task RunAsync_never_touches_the_faucet_on_mainnet()
    {
        var harness = GenesisHarness.Create(ChainNetwork.Mainnet);
        harness.WithFaucet(nativeBalanceBaseUnits: "0");

        var outcome = await harness.RunAsync();

        outcome.IsError.Should().BeFalse(outcome.Message);
        harness.FaucetDispenses.Should().Be(0, "a mainnet faucet dispense would be real value");
    }

    [Fact]
    public async Task RunAsync_skips_the_faucet_when_the_reserve_is_already_funded()
    {
        var harness = GenesisHarness.Create(ChainNetwork.Devnet);
        harness.WithFaucet(nativeBalanceBaseUnits: "50000000");

        await harness.RunAsync();

        harness.FaucetDispenses.Should().Be(0);
    }

    [Fact]
    public async Task GetStateAsync_reads_circulating_supply_as_reserve_outflow()
    {
        var harness = GenesisHarness.Create();
        await harness.RunAsync();
        harness.Store.SeedVerifiedReadProjection();
        harness.WithLedgerPartitions(
            ("contributor", Declared: 500_000, Issued: 120_000, Held: 30_000, Returned: 20_000),
            ("investor", Declared: 400_000, Issued: 60_000, Held: 0, Returned: 0));

        var state = await harness.Genesis.GetStateAsync();

        state.IsError.Should().BeFalse(state.Message);
        state.Result!.Supply.Circulating.Should().Be(
            new BigInteger(160_000), "circulating supply is issued minus returned across every partition");
        state.Result.Supply.Held.Should().Be(new BigInteger(30_000));
        state.Result.Supply.Total.Should().Be(new BigInteger(PlatformTokenGenesisDefaults.TotalBaseUnits));
        state.Result.Supply.InReserve.Should().Be(
            new BigInteger(PlatformTokenGenesisDefaults.TotalBaseUnits) - new BigInteger(160_000));
    }

    [Fact]
    public async Task GetStateAsync_starts_at_zero_circulating_supply_before_anything_is_issued()
    {
        var harness = GenesisHarness.Create();
        await harness.RunAsync();
        harness.Store.SeedVerifiedReadProjection();

        var state = await harness.Genesis.GetStateAsync();

        state.IsError.Should().BeFalse(state.Message);
        state.Result!.Supply.Circulating.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task GetStateAsync_reports_supply_unavailable_rather_than_zero_when_the_ledger_cannot_be_read()
    {
        var harness = GenesisHarness.Create();
        await harness.RunAsync();
        harness.Store.SeedVerifiedReadProjection();
        harness.WithoutLedgerRow();

        var state = await harness.Genesis.GetStateAsync();

        state.IsError.Should().BeTrue("a fabricated zero would price the unit off a supply nobody knows");
        state.Code.Should().Be(PlatformTokenGenesisCodes.SupplyUnavailable);
    }

    [Fact]
    public async Task GetStateAsync_says_so_when_genesis_has_never_run()
    {
        var harness = GenesisHarness.Create();

        var state = await harness.Genesis.GetStateAsync();

        state.IsError.Should().BeTrue();
        state.Code.Should().Be(PlatformTokenGenesisCodes.NotGenesised);
    }

    [Fact]
    public async Task Missing_actual_operator_credential_cannot_freeze_a_definition()
    {
        var harness = GenesisHarness.Create();
        (await harness.Genesis.RunAsync(Request(), OperatorUserId)).IsError.Should().BeTrue();
        harness.Store.Rows.Should().BeEmpty();
        harness.Minter.MintCount.Should().Be(0);
    }

    [Fact]
    public async Task Legacy_active_asset_text_cannot_be_reissued_or_reported_verified()
    {
        var harness = GenesisHarness.Create();
        await harness.RunAsync();
        var row = harness.Store.Single();
        row.Status = PlatformToken.StatusActive;
        row.AssetId = AssetId;
        (await harness.RunAsync()).Code.Should().Be(PlatformTokenGenesisCodes.EvidenceRequired);
        (await harness.Genesis.GetStateAsync()).Code.Should().Be(PlatformTokenGenesisCodes.EvidenceRequired);
        harness.Minter.MintCount.Should().Be(1);
    }

    private static SigningCredentialWitness OperatorCredential() => new(1, "Jwt", OperatorUserId, null, null,
        DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(10), null, null, [], false,
        TokenUse: AzoaClaims.TokenUseNodeOperator, OperatorCredentialRevision: 1, OperatorSessionRevision: 1);

    private static PlatformTokenGenesisRequest Request(
        ulong? total = null,
        int? decimals = null,
        string? unitName = null,
        string? expectedNetwork = null) => new()
    {
        Total = total,
        Decimals = decimals,
        UnitName = unitName,
        ExpectedNetwork = expectedNetwork,
        ConfirmIrreversible = true,
    };
    private sealed class GenesisHarness
    {
        private readonly Mock<IPlatformReserve> _reserve = new();
        private readonly Mock<IBlockchainProviderFactory> _factory = new();
        private readonly ChainNetwork _network;

        private GenesisHarness(ChainNetwork network)
        {
            _network = network;

            _reserve
                .Setup(r => r.GetAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<ReserveAccount>.Success(
                    new ReserveAccount(Chain, ReserveAddress, IsFeePayer: true)));

            _reserve
                .Setup(r => r.GetBalanceAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<string>.Success("50000000"));

            WithLedgerPartitions();

            _factory.SetupGet(f => f.DefaultNetwork).Returns(network);
            WithFaucet("50000000");

            Genesis = new PlatformTokenGenesis(_reserve.Object, Minter, Store, _factory.Object);
        }

        public static GenesisHarness Create(ChainNetwork network = ChainNetwork.Devnet) => new(network);

        public PlatformTokenGenesis Genesis { get; }

        public RecordingMinter Minter { get; } = new();

        public InMemoryPlatformTokenStore Store { get; } = new();

        public int FaucetDispenses { get; private set; }

        public Task<AZOAResult<PlatformTokenGenesisOutcome>> RunAsync(PlatformTokenGenesisRequest? request = null)
        {
            request ??= Request();
            request.AuthenticationWitness = OperatorCredential();
            return Genesis.RunAsync(request, OperatorUserId);
        }

        public void WithoutReserveAccount() =>
            _reserve
                .Setup(r => r.GetAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<ReserveAccount>.Failure("No platform key is configured for 'Algorand'."));

        public void WithoutLedgerRow() =>
            _reserve
                .Setup(r => r.GetLedgerAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<PlatformReserveLedger>.Failure("No reserve ledger row exists."));

        public void WithLedgerPartitions(
            params (string Name, long Declared, long Issued, long Held, long Returned)[] partitions)
        {
            var counters = partitions.ToDictionary(
                p => p.Name,
                p => new ReservePartitionCounters(
                    new BigInteger(p.Declared),
                    new BigInteger(p.Issued),
                    new BigInteger(p.Held),
                    new BigInteger(p.Returned)),
                StringComparer.Ordinal);

            var row = new PlatformReserveLedger
            {
                Id = "ledger",
                AssetId = AssetId,
                Chain = Chain,
                Network = _network.ToString(),
                ReserveAddress = ReserveAddress,
                UnitName = PlatformTokenGenesisDefaults.UnitName,
                Decimals = PlatformTokenGenesisDefaults.Decimals,
                TotalSupply = PlatformTokenGenesisDefaults.TotalBaseUnits.ToString(CultureInfo.InvariantCulture),
                Partitions = ReservePartitionCodec.Encode(counters),
            };

            _reserve
                .Setup(r => r.GetLedgerAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<PlatformReserveLedger>.Success(row));
        }
        public void WithFaucet(string nativeBalanceBaseUnits)
        {
            _reserve
                .Setup(r => r.GetBalanceAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<string>.Success(nativeBalanceBaseUnits));

            var provider = new Mock<IBlockchainProvider>();
            var module = new Mock<IFungibleCreationModule>();
            provider.Setup(p => p.TryGetModule<IFungibleCreationModule>(out It.Ref<IFungibleCreationModule?>.IsAny))
                .Returns((out IFungibleCreationModule? value) => { value = module.Object; return true; });
            provider.SetupGet(p => p.SupportsFaucet).Returns(true);
            provider
                .Setup(p => p.DispenseFromFaucetAsync(
                    It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<FaucetDispenseResult>.Success(
                    new FaucetDispenseResult("tx-1", IsClientSide: false, "dispensed")))
                .Callback(() => FaucetDispenses++);

            _factory
                .Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(provider.Object);
        }
    }
    private sealed class RecordingMinter : IPlatformTokenMinter
    {
        private string? _failure;

        public int MintCount { get; private set; }
        public bool ReportCompletedWithoutProjection { get; set; }

        public string? LastOperationKey { get; private set; }

        public FungibleTokenCreateRequest? LastRequest { get; private set; }

        public void FailWith(string reason) => _failure = reason;

        public void Succeed() => _failure = null;

        public Task<AZOAResult<FungibleTokenResult>> MintIntoReserveAsync(
            Guid operatorUserId,
            FungibleTokenCreateRequest request,
            string operationKey,
            CancellationToken ct = default)
        {
            MintCount++;
            LastOperationKey = operationKey;
            LastRequest = request;

            return Task.FromResult(_failure is null
                ? AZOAResult<FungibleTokenResult>.Success(new FungibleTokenResult
                {
                    UserId = operatorUserId,
                    AssetId = ReportCompletedWithoutProjection ? AssetId : string.Empty,
                    WalletAddress = ReserveAddress,
                    Operation = new FungibleCreationView(FungibleCreationIdentity.OperationId(operatorUserId, null, operationKey), 0,
                        ReportCompletedWithoutProjection ? "Completed" : "AwaitingConsent", Chain, request.Network.ToString()!,
                        ReportCompletedWithoutProjection ? AssetId : null, null, null, null, Guid.Empty, ReserveAddress, false,
                        Purpose: "PlatformGenesis"),
                })
                : AZOAResult<FungibleTokenResult>.Failure(_failure));
        }
    }
    private sealed class InMemoryPlatformTokenStore : IPlatformTokenStore
    {
        private readonly Dictionary<string, PlatformToken> _rows = new(StringComparer.Ordinal);

        public bool FailReads { get; set; }

        public bool FailUpdates { get; set; }

        public IReadOnlyCollection<PlatformToken> Rows => _rows.Values.ToList();

        public PlatformToken Single() => _rows.Values.Single();

        private string? _verifiedProjectionHash;

        public void SeedVerifiedReadProjection()
        {
            var row = Single();
            row.Status = PlatformToken.StatusActive;
            row.AssetId = AssetId;
            row.CreationOperationId = FungibleCreationIdentity.OperationId(OperatorUserId, null, row.GenesisOperationKey);
            row.ActivatedAt = DateTimeOffset.UtcNow;
            _verifiedProjectionHash = CanonicalJson.Hash(row);
        }

        public Task<bool> HasVerifiedIssuanceAsync(PlatformToken row, CancellationToken ct = default)
            => Task.FromResult(_verifiedProjectionHash is not null && CanonicalJson.Hash(row) == _verifiedProjectionHash);


        public Task<AZOAResult<IReadOnlyList<PlatformToken>>> ListForNetworkAsync(
            string network, CancellationToken ct = default)
        {
            if (FailReads)
                return Task.FromResult(
                    AZOAResult<IReadOnlyList<PlatformToken>>.Failure("Platform-token read failed: transport error."));

            IReadOnlyList<PlatformToken> rows = _rows.Values
                .Where(row => string.Equals(row.Network, network, StringComparison.Ordinal))
                .Select(Clone)
                .ToList();

            return Task.FromResult(AZOAResult<IReadOnlyList<PlatformToken>>.Success(rows));
        }

        public Task<AZOAResult<PlatformTokenClaim>> ClaimIfAbsentAsync(
            PlatformToken row, CancellationToken ct = default, SigningCredentialWitness? credential = null)
        {
            if (_rows.TryGetValue(row.Id, out var existing))
                return Task.FromResult(AZOAResult<PlatformTokenClaim>.Success(
                    new PlatformTokenClaim(Clone(existing), Created: false)));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<PlatformTokenClaim>.Success(
                new PlatformTokenClaim(Clone(row), Created: true)));
        }

        public Task<AZOAResult<PlatformToken>> UpdateIfVersionAsync(
            PlatformToken row, long expectedVersion, CancellationToken ct = default)
        {
            if (FailUpdates)
                return Task.FromResult(
                    AZOAResult<PlatformToken>.Failure("Platform-token update failed: transport error."));

            if (!_rows.TryGetValue(row.Id, out var existing) || existing.Version != expectedVersion)
                return Task.FromResult(AZOAResult<PlatformToken>.Failure(
                    $"Platform-token row '{row.Id}' changed underneath this update."));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<PlatformToken>.Success(Clone(row)));
        }

        private static PlatformToken Clone(PlatformToken row) => new()
        {
            Id = row.Id,
            Chain = row.Chain,
            Network = row.Network,
            Status = row.Status,
            AssetId = row.AssetId,
            CreationOperationId = row.CreationOperationId,
            Name = row.Name,
            UnitName = row.UnitName,
            Decimals = row.Decimals,
            TotalSupply = row.TotalSupply,
            ReserveAddress = row.ReserveAddress,
            GenesisOperationKey = row.GenesisOperationKey,
            GenesisUserId = row.GenesisUserId,
            LastFailureReason = row.LastFailureReason,
            Version = row.Version,
            ClaimedAt = row.ClaimedAt,
            ActivatedAt = row.ActivatedAt,
            UpdatedAt = row.UpdatedAt,
        };
    }
}
