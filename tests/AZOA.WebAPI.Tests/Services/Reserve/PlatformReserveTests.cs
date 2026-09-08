// SPDX-License-Identifier: UNLICENSED
// W3.1 — the platform reserve: address resolution, the named-partition
// oversubscription check, and the mint-into-reserve target on the fungible-token seam.

using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Moq;
using Xunit;
using AlgorandAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Services.Reserve;

public class PlatformReserveTests
{
    private const string Chain = "Algorand";
    private const string AssetId = "1001";
    private const string ContributorPartition = "contributor";
    private const string InvestorPartition = "investor";

    // ── Account resolution ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAccountAsync_resolves_the_address_derived_from_the_platform_key()
    {
        var harness = ReserveHarness.WithPlatformKey();

        var account = await harness.Reserve.GetAccountAsync(Chain);

        account.IsError.Should().BeFalse(account.Message);
        account.Result!.Address.Should().Be(harness.ExpectedAddress);
        account.Result.IsFeePayer.Should().BeTrue("the reserve carries the fee-payer role today");
    }

    [Fact]
    public async Task GetAccountAsync_fails_closed_when_the_chain_has_no_platform_key()
    {
        var harness = ReserveHarness.WithoutPlatformKey();

        var account = await harness.Reserve.GetAccountAsync(Chain);

        account.IsError.Should().BeTrue();
        account.Result.Should().BeNull();
        account.Message.Should().Contain(Chain);
    }

    [Fact]
    public async Task GetAccountAsync_refuses_a_chain_the_operator_never_approved()
    {
        var harness = ReserveHarness.WithPlatformKey(options =>
            options.Chains = new List<string> { "Solana" });

        var account = await harness.Reserve.GetAccountAsync(Chain);

        account.IsError.Should().BeTrue();
        account.Message.Should().Contain("Reserve:Chains");
    }

    [Fact]
    public async Task GetAccountAsync_refuses_when_the_derived_address_contradicts_the_declared_one()
    {
        var harness = ReserveHarness.WithPlatformKey(options =>
            options.ExpectedAddresses = new Dictionary<string, string>
            {
                [Chain] = "SOMEONEELSESADDRESSTHATISNOTTHERESERVEACCOUNTAAAAAAAAAAAAAAAAAAAAAAAA",
            });

        var account = await harness.Reserve.GetAccountAsync(Chain);

        account.IsError.Should().BeTrue();
        account.Message.Should().Contain("ExpectedAddresses");
    }

    // ── Registration + partition declaration ──────────────────────────────────

    [Fact]
    public async Task RegisterAssetAsync_is_idempotent_and_never_resets_counters()
    {
        var harness = ReserveHarness.WithPlatformKey();
        await harness.RegisterAsync(totalSupply: 1_000);
        await harness.DeclareAsync((ContributorPartition, 400));
        await harness.IssueAsync(ContributorPartition, 100, "op-1");

        var second = await harness.RegisterAsync(totalSupply: 1_000);

        second.IsError.Should().BeFalse(second.Message);
        var counters = await harness.CountersAsync(ContributorPartition);
        counters.Issued.Should().Be(new BigInteger(100), "a re-registration must not wipe committed units");
    }

    [Fact]
    public async Task DeclarePartitionsAsync_refuses_a_declaration_wider_than_the_supply()
    {
        var harness = ReserveHarness.WithPlatformKey();
        await harness.RegisterAsync(totalSupply: 1_000);

        var declared = await harness.DeclareAsync((ContributorPartition, 600), (InvestorPartition, 600));

        declared.IsError.Should().BeTrue();
        declared.Message.Should().Contain("total supply");
    }

    [Fact]
    public async Task DeclarePartitionsAsync_refuses_to_shrink_below_what_is_already_committed()
    {
        var harness = ReserveHarness.WithPlatformKey();
        await harness.RegisterAsync(totalSupply: 1_000);
        await harness.DeclareAsync((ContributorPartition, 400));
        await harness.IssueAsync(ContributorPartition, 300, "op-1");

        var shrunk = await harness.DeclareAsync((ContributorPartition, 200));

        shrunk.IsError.Should().BeTrue();
        shrunk.Message.Should().Contain("already committed");
    }

    // ── The oversubscription check ────────────────────────────────────────────

    [Fact]
    public async Task DrawAsync_refuses_a_partition_nobody_declared()
    {
        var harness = ReserveHarness.WithPlatformKey();
        await harness.RegisterAsync(totalSupply: 1_000);

        var draw = await harness.IssueAsync("invented", 1, "op-1");

        draw.IsError.Should().BeTrue("auto-creating a partition would invent unlimited availability");
        draw.Message.Should().Contain("not declared");
    }

    [Fact]
    public async Task DrawAsync_refuses_an_issue_that_would_oversubscribe_the_partition()
    {
        var harness = ReserveHarness.WithPlatformKey();
        await harness.RegisterAsync(totalSupply: 1_000);
        await harness.DeclareAsync((ContributorPartition, 400));
        await harness.IssueAsync(ContributorPartition, 350, "op-1");

        var draw = await harness.IssueAsync(ContributorPartition, 100, "op-2");

        draw.IsError.Should().BeTrue();
        draw.Message.Should().Contain("oversubscribed");
        (await harness.CountersAsync(ContributorPartition)).Issued.Should().Be(new BigInteger(350));
    }

    [Fact]
    public async Task DrawAsync_refuses_when_a_hold_and_an_issue_together_exceed_the_partition()
    {
        var harness = ReserveHarness.WithPlatformKey();
        await harness.RegisterAsync(totalSupply: 1_000);
        await harness.DeclareAsync((ContributorPartition, 400));
        await harness.DrawAsync(ContributorPartition, ReserveDrawKind.Hold, 250, "hold-1");

        var draw = await harness.IssueAsync(ContributorPartition, 200, "op-2");

        draw.IsError.Should().BeTrue("held units are already spoken for");
        draw.Message.Should().Contain("oversubscribed");
    }

    [Fact]
    public async Task DrawAsync_release_moves_held_units_to_issued_without_freeing_availability()
    {
        var harness = ReserveHarness.WithPlatformKey();
        await harness.RegisterAsync(totalSupply: 1_000);
        await harness.DeclareAsync((ContributorPartition, 400));
        await harness.DrawAsync(ContributorPartition, ReserveDrawKind.Hold, 250, "hold-1");

        var released = await harness.DrawAsync(ContributorPartition, ReserveDrawKind.ReleaseHold, 250, "rel-1");

        released.IsError.Should().BeFalse(released.Message);
        var counters = await harness.CountersAsync(ContributorPartition);
        counters.Held.Should().Be(BigInteger.Zero);
        counters.Issued.Should().Be(new BigInteger(250));
        counters.Available.Should().Be(new BigInteger(150));
    }

    [Fact]
    public async Task DrawAsync_cancel_returns_held_units_to_availability()
    {
        var harness = ReserveHarness.WithPlatformKey();
        await harness.RegisterAsync(totalSupply: 1_000);
        await harness.DeclareAsync((ContributorPartition, 400));
        await harness.DrawAsync(ContributorPartition, ReserveDrawKind.Hold, 250, "hold-1");

        await harness.DrawAsync(ContributorPartition, ReserveDrawKind.CancelHold, 250, "cancel-1");

        (await harness.CountersAsync(ContributorPartition)).Available.Should().Be(new BigInteger(400));
    }

    [Fact]
    public async Task DrawAsync_replays_a_repeated_operation_key_instead_of_double_counting()
    {
        var harness = ReserveHarness.WithPlatformKey();
        await harness.RegisterAsync(totalSupply: 1_000);
        await harness.DeclareAsync((ContributorPartition, 400));
        await harness.IssueAsync(ContributorPartition, 100, "op-1");

        var replay = await harness.IssueAsync(ContributorPartition, 100, "op-1");

        replay.IsError.Should().BeFalse(replay.Message);
        replay.Result!.Replayed.Should().BeTrue();
        (await harness.CountersAsync(ContributorPartition)).Issued.Should().Be(new BigInteger(100));
    }

    [Fact]
    public async Task DrawAsync_requires_an_operation_key()
    {
        var harness = ReserveHarness.WithPlatformKey();
        await harness.RegisterAsync(totalSupply: 1_000);
        await harness.DeclareAsync((ContributorPartition, 400));

        var draw = await harness.Reserve.DrawAsync(new ReserveDrawRequest(
            Chain, AssetId, ContributorPartition, ReserveDrawKind.Issue, new BigInteger(10), "  "));

        draw.IsError.Should().BeTrue();
        draw.Message.Should().Contain("operation key");
    }

    [Fact]
    public async Task DrawAsync_fails_closed_when_the_asset_has_no_ledger_row()
    {
        var harness = ReserveHarness.WithPlatformKey();

        var draw = await harness.IssueAsync(ContributorPartition, 10, "op-1");

        draw.IsError.Should().BeTrue("an unregistered asset has no partitions to police it");
    }

    [Fact]
    public async Task Partition_names_are_normalised_so_case_cannot_open_a_second_budget()
    {
        var harness = ReserveHarness.WithPlatformKey();
        await harness.RegisterAsync(totalSupply: 1_000);
        await harness.DeclareAsync((ContributorPartition, 100));
        await harness.IssueAsync("Contributor", 100, "op-1");

        var draw = await harness.IssueAsync("CONTRIBUTOR", 1, "op-2");

        draw.IsError.Should().BeTrue();
        draw.Message.Should().Contain("oversubscribed");
    }

    // ── Codec ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_refuses_a_malformed_partitions_blob_rather_than_reading_it_as_empty()
    {
        var malformed = JsonSerializer.SerializeToElement(new[] { "not", "an", "object" });

        var decoded = ReservePartitionCodec.Decode(malformed, out var error);

        decoded.Should().BeNull("reading unreadable partitions as 'no commitments' hands out the whole supply");
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void Decode_reads_back_exactly_what_Encode_wrote_for_a_supply_wider_than_i64()
    {
        var huge = BigInteger.Parse("170141183460469231731687303715884105728");
        var encoded = ReservePartitionCodec.Encode(new Dictionary<string, ReservePartitionCounters>
        {
            [ContributorPartition] = new(huge, BigInteger.One, BigInteger.Zero, BigInteger.Zero),
        });

        var decoded = ReservePartitionCodec.Decode(encoded, out var error);

        error.Should().BeEmpty();
        decoded![ContributorPartition].Declared.Should().Be(huge);
        decoded[ContributorPartition].Issued.Should().Be(BigInteger.One);
    }

    // ── Mint-into-reserve on the fungible-token seam (Q-X3) ───────────────────

    [Fact]
    public async Task FungibleCreate_into_the_reserve_stamps_the_reserve_on_every_asa_authority()
    {
        var harness = ReserveHarness.WithPlatformKey();
        var mint = new FungibleMintHarness(harness);

        var created = await mint.CreateAsync();

        created.IsError.Should().BeFalse(created.Message);
        mint.CapturedCreatorAddress.Should().Be(harness.ExpectedAddress);
        mint.CapturedManagerAddress.Should().Be(harness.ExpectedAddress);
        mint.CapturedReserveAddress.Should().Be(harness.ExpectedAddress);
        mint.CapturedFreezeAddress.Should().Be(harness.ExpectedAddress);
        mint.CapturedClawbackAddress.Should().Be(harness.ExpectedAddress);
        mint.CapturedCreatorAddress.Should().NotBe(FungibleMintHarness.CallerWalletAddress);
    }

    [Fact]
    public async Task FungibleCreate_into_the_reserve_opens_the_asset_ledger_row()
    {
        var harness = ReserveHarness.WithPlatformKey();
        var mint = new FungibleMintHarness(harness);

        var created = await mint.CreateAsync();

        created.Code.Should().BeNull("the ledger row was written, so there is nothing to flag");
        var ledger = await harness.Reserve.GetLedgerAsync(Chain, created.Result!.AssetId);
        ledger.IsError.Should().BeFalse(ledger.Message);
        ledger.Result!.ReserveAddress.Should().Be(harness.ExpectedAddress);
        ledger.Result.TotalSupply.Should().Be(FungibleMintHarness.TotalSupply.ToString());
    }

    [Fact]
    public async Task FungibleCreate_fails_closed_and_broadcasts_nothing_when_no_reserve_resolves()
    {
        var harness = ReserveHarness.WithoutPlatformKey();
        var mint = new FungibleMintHarness(harness);

        var created = await mint.CreateAsync();

        created.IsError.Should().BeTrue();
        mint.CreateAsaCallCount.Should().Be(0, "an unresolvable reserve must refuse before any broadcast");
        mint.FailedIdempotencyKeys.Should().ContainSingle();
    }

    [Fact]
    public async Task FungibleCreate_with_the_legacy_caller_wallet_target_still_names_the_caller_wallet()
    {
        var harness = ReserveHarness.WithPlatformKey(options =>
            options.FungibleMintTarget = ReserveMintTargets.CallerWallet);
        var mint = new FungibleMintHarness(harness);

        var created = await mint.CreateAsync();

        created.IsError.Should().BeFalse(created.Message);
        mint.CapturedCreatorAddress.Should().Be(FungibleMintHarness.CallerWalletAddress);
        var ledger = await harness.Reserve.GetLedgerAsync(Chain, created.Result!.AssetId);
        ledger.IsError.Should().BeTrue("a caller-held asset has no reserve budget to partition");
    }

    // ── Harnesses ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A reserve wired to an in-memory ledger and a custody double that hands back a real
    /// Algorand key, so the address under test is genuinely derived rather than asserted.
    /// </summary>
    private sealed class ReserveHarness
    {
        private ReserveHarness(
            PlatformReserveService reserve,
            InMemoryReserveLedgerStore ledger,
            ReserveOptions options,
            string expectedAddress,
            IBlockchainProviderFactory providerFactory)
        {
            Reserve = reserve;
            Ledger = ledger;
            Options = options;
            ExpectedAddress = expectedAddress;
            ProviderFactory = providerFactory;
        }

        public PlatformReserveService Reserve { get; }
        public InMemoryReserveLedgerStore Ledger { get; }
        public ReserveOptions Options { get; }
        public string ExpectedAddress { get; }
        public IBlockchainProviderFactory ProviderFactory { get; }

        public static ReserveHarness WithPlatformKey(Action<ReserveOptions>? configure = null)
        {
            var account = new AlgorandAccount();
            var key = (byte[])account.KeyPair.ClearTextPrivateKey.Clone();
            return Build(key, account.Address.ToString(), configure);
        }

        public static ReserveHarness WithoutPlatformKey(Action<ReserveOptions>? configure = null) =>
            Build(platformKey: null, expectedAddress: string.Empty, configure);

        private static ReserveHarness Build(
            byte[]? platformKey, string expectedAddress, Action<ReserveOptions>? configure)
        {
            var options = new ReserveOptions();
            configure?.Invoke(options);

            var ledger = new InMemoryReserveLedgerStore();
            var factory = new Mock<IBlockchainProviderFactory>();
            factory.SetupGet(f => f.DefaultNetwork).Returns(ChainNetwork.Devnet);

            var reserve = new PlatformReserveService(
                new FakeKeyCustodyService(platformKey), factory.Object, ledger, options);

            return new ReserveHarness(reserve, ledger, options, expectedAddress, factory.Object);
        }

        public Task<AZOAResult<PlatformReserveLedger>> RegisterAsync(long totalSupply) =>
            Reserve.RegisterAssetAsync(new ReserveAssetRegistration(
                Chain, AssetId, "UNIT", 2, new BigInteger(totalSupply)));

        public Task<AZOAResult<PlatformReserveLedger>> DeclareAsync(
            params (string Name, long Declared)[] partitions) =>
            Reserve.DeclarePartitionsAsync(
                Chain,
                AssetId,
                partitions.ToDictionary(p => p.Name, p => new BigInteger(p.Declared)));

        public Task<AZOAResult<ReserveDrawOutcome>> IssueAsync(
            string partition, long amount, string operationKey) =>
            DrawAsync(partition, ReserveDrawKind.Issue, amount, operationKey);

        public Task<AZOAResult<ReserveDrawOutcome>> DrawAsync(
            string partition, ReserveDrawKind kind, long amount, string operationKey) =>
            Reserve.DrawAsync(new ReserveDrawRequest(
                Chain, AssetId, partition, kind, new BigInteger(amount), operationKey));

        public async Task<ReservePartitionCounters> CountersAsync(string partition)
        {
            var row = await Reserve.GetLedgerAsync(Chain, AssetId);
            row.IsError.Should().BeFalse(row.Message);
            var partitions = ReservePartitionCodec.Decode(row.Result!.Partitions, out var error);
            error.Should().BeEmpty();
            return partitions![ReservePartitionCodec.Normalize(partition)];
        }
    }

    /// <summary>
    /// Drives <see cref="FungibleTokenManager"/> with every dependency doubled, capturing the
    /// five addresses handed to <c>CreateASAAsync</c> — the addresses that decide who holds
    /// authority over a project's asset.
    /// </summary>
    private sealed class FungibleMintHarness
    {
        public const string CallerWalletAddress = "CALLERWALLETADDRESSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        public const long TotalSupply = 1_000_000;

        private readonly FungibleTokenManager _manager;

        public FungibleMintHarness(ReserveHarness harness)
        {
            var valueAccess = new Mock<IValueAccessService>();
            valueAccess
                .Setup(v => v.RequireValueAccessAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AZOAResult<bool> { Result = true });

            var wallet = new AZOA.WebAPI.Models.Wallet
            {
                Id = Guid.NewGuid(),
                UserId = UserId,
                ChainType = Chain,
                Address = CallerWalletAddress,
            };
            var walletStore = new Mock<IWalletStore>();
            walletStore
                .Setup(s => s.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new IWallet[] { wallet } });

            var asa = new Mock<IAlgorandASAModule>();
            asa.Setup(m => m.CreateASAAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<SigningContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AZOAResult<string> { Result = AssetId })
                .Callback<string, string, int, int, string, string, string, string, string,
                    SigningContext, CancellationToken>(
                    (_, _, _, _, manager, reserve, freeze, clawback, creator, _, _) =>
                    {
                        CreateAsaCallCount++;
                        CapturedManagerAddress = manager;
                        CapturedReserveAddress = reserve;
                        CapturedFreezeAddress = freeze;
                        CapturedClawbackAddress = clawback;
                        CapturedCreatorAddress = creator;
                    });

            IAlgorandASAModule? asaModule = asa.Object;
            var provider = new Mock<IBlockchainProvider>();
            provider.Setup(p => p.TryGetModule(out asaModule)).Returns(true);

            var factory = new Mock<IBlockchainProviderFactory>();
            factory.SetupGet(f => f.DefaultNetwork).Returns(ChainNetwork.Devnet);
            factory.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(provider.Object);

            var idempotency = new Mock<IIdempotencyStore>();
            idempotency
                .Setup(s => s.TryClaimAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IdempotencyClaim(true, new IdempotencyRecord()));
            idempotency
                .Setup(s => s.FailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((key, _, _) => FailedIdempotencyKeys.Add(key))
                .Returns(Task.CompletedTask);
            idempotency
                .Setup(s => s.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _manager = new FungibleTokenManager(
                valueAccess.Object,
                new Mock<IWalletManager>().Object,
                walletStore.Object,
                factory.Object,
                idempotency.Object,
                harness.Reserve,
                harness.Options);
        }

        public static Guid UserId { get; } = Guid.NewGuid();

        public int CreateAsaCallCount { get; private set; }
        public string? CapturedManagerAddress { get; private set; }
        public string? CapturedReserveAddress { get; private set; }
        public string? CapturedFreezeAddress { get; private set; }
        public string? CapturedClawbackAddress { get; private set; }
        public string? CapturedCreatorAddress { get; private set; }
        public List<string> FailedIdempotencyKeys { get; } = new();

        public Task<AZOAResult<FungibleTokenResult>> CreateAsync() =>
            _manager.CreateAsync(
                UserId,
                new FungibleTokenCreateRequest
                {
                    ChainType = Chain,
                    Name = "Project Units",
                    UnitName = "PU",
                    Total = TotalSupply,
                    Decimals = 2,
                },
                UserId,
                clientIdempotencyKey: null,
                apiKeyId: "api-key-1");
    }

    /// <summary>
    /// Custody double that hands the reserve a real per-chain key (or none). It mirrors the
    /// production contract that matters here: no key configured means no result at all,
    /// never a fabricated address.
    /// </summary>
    private sealed class FakeKeyCustodyService : IKeyCustodyService
    {
        private readonly byte[]? _platformKey;

        public FakeKeyCustodyService(byte[]? platformKey) => _platformKey = platformKey;

        public async Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(
            string chainType, bool isPlatformContext, SigningContext ctx, Func<byte[], Task<T>> sign)
        {
            if (!isPlatformContext)
                return AZOAResult<T>.Failure("Platform authority was not asserted.");
            if (_platformKey is null)
                return AZOAResult<T>.Failure(
                    $"No platform key is configured for chain '{chainType}'.");

            return AZOAResult<T>.Success(await sign(_platformKey));
        }

        public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(
            bool isPlatformContext, Func<byte[], Task<T>> sign) =>
            WithPlatformSigningKeyAsync(
                KeyCustodyService.LegacyPlatformChainType, isPlatformContext, SigningContext.Platform, sign);

        public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(
            bool isPlatformContext, SigningContext ctx, Func<byte[], Task<T>> sign) =>
            WithPlatformSigningKeyAsync(
                KeyCustodyService.LegacyPlatformChainType, isPlatformContext, ctx, sign);

        public Task<AZOAResult<T>> WithSigningKeyAsync<T>(
            Guid walletId, Guid userId, Func<byte[], Task<T>> sign) =>
            Task.FromResult(AZOAResult<T>.Failure("Per-user signing is not exercised by these tests."));

        public Task<AZOAResult<T>> WithSigningKeyAsync<T>(SigningContext ctx, Func<byte[], Task<T>> sign) =>
            Task.FromResult(AZOAResult<T>.Failure("Per-user signing is not exercised by these tests."));

        public Task<AZOAResult<bool>> CanSignAsync(Guid walletId, Guid userId) =>
            Task.FromResult(AZOAResult<bool>.Success(false));

        public AZOAResult<IWallet> RewrapAsync(
            IWallet wallet, WalletKeyService oldKeyService, WalletKeyService newKeyService) =>
            AZOAResult<IWallet>.Failure("Rewrap is not exercised by these tests.");
    }

    /// <summary>
    /// In-memory ledger with the same compare-and-swap contract as the Surreal store: a
    /// write whose expected version has moved loses, it does not overwrite.
    /// </summary>
    private sealed class InMemoryReserveLedgerStore : IPlatformReserveLedgerStore
    {
        private readonly Dictionary<string, PlatformReserveLedger> _rows = new(StringComparer.Ordinal);

        public Task<AZOAResult<PlatformReserveLedger?>> GetAsync(
            string recordId, CancellationToken ct = default) =>
            Task.FromResult(new AZOAResult<PlatformReserveLedger?>
            {
                Result = _rows.TryGetValue(recordId, out var row) ? Clone(row) : null,
            });

        public Task<AZOAResult<PlatformReserveLedgerClaim>> CreateIfAbsentAsync(
            PlatformReserveLedger row, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(row.Id, out var existing))
                return Task.FromResult(AZOAResult<PlatformReserveLedgerClaim>.Success(
                    new PlatformReserveLedgerClaim(Clone(existing), Created: false)));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<PlatformReserveLedgerClaim>.Success(
                new PlatformReserveLedgerClaim(Clone(row), Created: true)));
        }

        public Task<AZOAResult<PlatformReserveLedger>> UpdateIfVersionAsync(
            PlatformReserveLedger row, long expectedVersion, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(row.Id, out var existing) || existing.Version != expectedVersion)
                return Task.FromResult(AZOAResult<PlatformReserveLedger>.Failure(
                    $"Reserve ledger row '{row.Id}' changed underneath this update."));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<PlatformReserveLedger>.Success(Clone(row)));
        }

        private static PlatformReserveLedger Clone(PlatformReserveLedger row) => new()
        {
            Id = row.Id,
            AssetId = row.AssetId,
            Chain = row.Chain,
            Network = row.Network,
            ReserveAddress = row.ReserveAddress,
            TenantId = row.TenantId,
            UnitName = row.UnitName,
            Decimals = row.Decimals,
            TotalSupply = row.TotalSupply,
            Partitions = row.Partitions,
            RecentDrawKeys = row.RecentDrawKeys?.ToArray(),
            Version = row.Version,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
        };
    }
}
