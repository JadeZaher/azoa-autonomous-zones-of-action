// SPDX-License-Identifier: UNLICENSED
// W3.4 — the HOLD primitive: the window that makes "this is yours in N days" true, the
// reserve-partition arithmetic behind it, and the controls that keep a graph from
// routing around either.

using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
// Both AZOA.WebAPI.Models and AZOA.WebAPI.Persistence.SurrealDb.Models declare a
// Wallet; the fake store hands back IWallet, which only the domain model implements.
using Wallet = AZOA.WebAPI.Models.Wallet;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Moq;
using Xunit;
using AlgorandAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Services.Hold;

public class PlatformHoldTests
{
    private const string Chain = "Algorand";
    private const string AssetId = "1001";
    private const string ForeignAssetId = "2002";
    private const string Partition = "contributor";
    private const string HoldId = "hold-alpha";
    private const string NextHoldId = "hold-alpha-worker";

    private static readonly Guid Beneficiary = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Worker = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid TenantOne = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid TenantTwo = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // ── Opening a hold commits, and commits only once ─────────────────────────

    [Fact]
    public async Task OpenAsync_parks_the_units_in_the_holding_account_and_commits_the_partition()
    {
        var harness = await HoldHarness.ReadyAsync();

        var opened = await harness.OpenAsync(HoldId, amount: 100);

        opened.IsError.Should().BeFalse(opened.Message);
        opened.Result!.Hold.State.Should().Be(nameof(HoldState.Held));
        opened.Result.Hold.HoldingAddress.Should().Be(
            harness.ReserveAddress, "a hold's units sit in the node's own custodial account");

        var counters = await harness.CountersAsync();
        counters.Held.Should().Be(new BigInteger(100));
        counters.Issued.Should().Be(BigInteger.Zero,
            "nothing has been issued to anyone while the window is open");
    }

    [Fact]
    public async Task OpenAsync_is_idempotent_on_the_hold_id_and_never_commits_twice()
    {
        var harness = await HoldHarness.ReadyAsync();

        await harness.OpenAsync(HoldId, amount: 100);
        var second = await harness.OpenAsync(HoldId, amount: 100);

        second.IsError.Should().BeFalse(second.Message);
        second.Result!.Replayed.Should().BeTrue();

        var counters = await harness.CountersAsync();
        counters.Held.Should().Be(new BigInteger(100), "a replayed open must commit nothing further");
    }

    [Fact]
    public async Task OpenAsync_refuses_a_replayed_hold_id_that_carries_different_terms()
    {
        var harness = await HoldHarness.ReadyAsync();
        await harness.OpenAsync(HoldId, amount: 100);

        var rewritten = await harness.OpenAsync(HoldId, amount: 250);

        rewritten.IsError.Should().BeTrue("replaying an id with new terms would rewrite a live commitment");
        var counters = await harness.CountersAsync();
        counters.Held.Should().Be(new BigInteger(100));
    }

    [Fact]
    public async Task OpenAsync_refuses_an_asset_the_reserve_has_no_record_of()
    {
        var harness = await HoldHarness.ReadyAsync();

        var opened = await harness.OpenAsync(HoldId, amount: 100, assetId: ForeignAssetId);

        opened.IsError.Should().BeTrue(
            "the holding account can only hold an asset the reserve actually holds");
        opened.Message.Should().Contain(ForeignAssetId);
    }

    [Fact]
    public async Task OpenAsync_refuses_a_partition_declared_by_another_tenant()
    {
        var harness = await HoldHarness.ReadyAsync(assetTenantId: TenantOne);

        var opened = await harness.OpenAsync(HoldId, amount: 100, actingTenantId: TenantTwo);

        opened.IsError.Should().BeTrue(
            "one tenant's declared budget is not another tenant's to draw from");
        var counters = await harness.CountersAsync();
        counters.Held.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task OpenAsync_refuses_more_than_the_partition_declared()
    {
        var harness = await HoldHarness.ReadyAsync(declared: 100);

        var opened = await harness.OpenAsync(HoldId, amount: 101);

        opened.IsError.Should().BeTrue("the partition cap is the node-side oversubscription check");
    }

    [Fact]
    public async Task OpenAsync_refuses_a_hold_funded_from_a_person_rather_than_pretending()
    {
        var harness = await HoldHarness.ReadyAsync();

        var opened = await harness.Holds.OpenAsync(new HoldOpenRequest(
            new HoldReference(Chain, HoldId),
            AssetId,
            new BigInteger(100),
            HoldFundingSource.Funder,
            Beneficiary,
            ReservePartition: Partition,
            FunderUserId: Worker));

        opened.IsError.Should().BeTrue();
        opened.Message.Should().Contain("nothing was held");
    }

    // ── The window ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReleaseAsync_refuses_before_the_window_elapses_and_moves_nothing()
    {
        var harness = await HoldHarness.ReadyAsync();
        await harness.OpenAsync(HoldId, amount: 100, releaseNotBefore: DateTimeOffset.UtcNow.AddDays(30));

        var released = await harness.ReleaseAsync(HoldId);

        released.IsError.Should().BeTrue("the hold window is the whole point of a hold");
        harness.Transfers.Should().BeEmpty();

        var counters = await harness.CountersAsync();
        counters.Held.Should().Be(new BigInteger(100));
        counters.Issued.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task ReleaseAsync_pays_the_beneficiary_once_the_window_has_elapsed()
    {
        var harness = await HoldHarness.ReadyAsync();
        await harness.OpenAsync(HoldId, amount: 100, releaseNotBefore: DateTimeOffset.UtcNow.AddSeconds(-1));

        var released = await harness.ReleaseAsync(HoldId);

        released.IsError.Should().BeFalse(released.Message);
        released.Result!.Hold.State.Should().Be(nameof(HoldState.Released));
        harness.Transfers.Should().ContainSingle();
        harness.Transfers[0].To.Should().Be(harness.BeneficiaryAddress);
        harness.Transfers[0].Amount.Should().Be(new BigInteger(100));

        var counters = await harness.CountersAsync();
        counters.Held.Should().Be(BigInteger.Zero);
        counters.Issued.Should().Be(new BigInteger(100));
    }

    [Fact]
    public async Task ReleaseAsync_prepares_the_beneficiary_before_moving_anything()
    {
        var harness = await HoldHarness.ReadyAsync();
        await harness.OpenAsync(HoldId, amount: 100);

        await harness.ReleaseAsync(HoldId);

        harness.Preparer.Calls.Should().ContainSingle(
            "the first release to a worker who has never held this asset fails on the rail without it");
        harness.Preparer.Calls[0].RecipientAddress.Should().Be(harness.BeneficiaryAddress);
        harness.Preparer.Calls[0].FeePayerAddress.Should().Be(harness.ReserveAddress);
    }

    [Fact]
    public async Task ReleaseAsync_refuses_while_the_beneficiary_account_is_still_settling()
    {
        var harness = await HoldHarness.ReadyAsync();
        harness.Preparer.PendingConfirmation = true;
        await harness.OpenAsync(HoldId, amount: 100);

        var released = await harness.ReleaseAsync(HoldId);

        released.IsError.Should().BeTrue();
        harness.Transfers.Should().BeEmpty("units must not be sent into an account that is not ready");
    }

    [Fact]
    public async Task ReleaseAsync_is_idempotent_and_never_pays_a_hold_twice()
    {
        var harness = await HoldHarness.ReadyAsync();
        await harness.OpenAsync(HoldId, amount: 100);

        await harness.ReleaseAsync(HoldId);
        var again = await harness.ReleaseAsync(HoldId);

        again.IsError.Should().BeFalse(again.Message);
        again.Result!.Replayed.Should().BeTrue();
        harness.Transfers.Should().ContainSingle("a second send would be a second payment");
    }

    [Fact]
    public async Task ReleaseAsync_reconciles_an_in_flight_payout_instead_of_resending_it()
    {
        var harness = await HoldHarness.ReadyAsync();
        await harness.OpenAsync(HoldId, amount: 100);

        // The rail accepted the payout but the answer never came back.
        harness.TransferOutcome = TransferOutcome.BroadcastThenTimeout;
        var first = await harness.ReleaseAsync(HoldId);
        first.IsError.Should().BeTrue();

        harness.TransferOutcome = TransferOutcome.Succeeds;
        harness.Confirmation = ChainConfirmation.Unknown;
        var second = await harness.ReleaseAsync(HoldId);

        second.IsError.Should().BeTrue("an unobserved payout must never be resent");
        harness.Transfers.Should().ContainSingle();
    }

    [Fact]
    public async Task ReleaseAsync_completes_a_hold_whose_payout_turns_out_to_have_landed()
    {
        var harness = await HoldHarness.ReadyAsync();
        await harness.OpenAsync(HoldId, amount: 100);

        harness.TransferOutcome = TransferOutcome.BroadcastThenTimeout;
        await harness.ReleaseAsync(HoldId);

        harness.Confirmation = ChainConfirmation.Confirmed;
        var reconciled = await harness.ReleaseAsync(HoldId);

        reconciled.IsError.Should().BeFalse(reconciled.Message);
        reconciled.Result!.Hold.State.Should().Be(nameof(HoldState.Released));
        harness.Transfers.Should().ContainSingle();
    }

    // ── Giving a hold back ────────────────────────────────────────────────────

    [Fact]
    public async Task RefundAsync_returns_the_commitment_to_its_partition_and_moves_nothing()
    {
        var harness = await HoldHarness.ReadyAsync();
        await harness.OpenAsync(HoldId, amount: 100);

        var refunded = await harness.RefundAsync(HoldId);

        refunded.IsError.Should().BeFalse(refunded.Message);
        refunded.Result!.Hold.State.Should().Be(nameof(HoldState.Refunded));
        harness.Transfers.Should().BeEmpty("a reserve-sourced hold's units never left the holding account");

        var counters = await harness.CountersAsync();
        counters.Held.Should().Be(BigInteger.Zero);
        counters.Available.Should().Be(new BigInteger(1000));
    }

    [Fact]
    public async Task RefundAsync_refuses_a_hold_whose_release_draw_already_committed()
    {
        var harness = await HoldHarness.ReadyAsync();
        await harness.OpenAsync(HoldId, amount: 100);

        // Park the hold in Releasing: the draw landed, the payout did not.
        harness.TransferOutcome = TransferOutcome.Fails;
        await harness.ReleaseAsync(HoldId);
        (await harness.StateOfAsync(HoldId)).Should().Be(nameof(HoldState.Releasing));

        var refunded = await harness.RefundAsync(HoldId);

        refunded.IsError.Should().BeTrue(
            "those units are already counted as issued; crediting the partition again hands the budget out twice");

        var counters = await harness.CountersAsync();
        counters.Issued.Should().Be(new BigInteger(100));
    }

    // ── Continuing a hold ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReleaseIntoHoldAsync_opens_the_next_hold_over_the_same_units()
    {
        var harness = await HoldHarness.ReadyAsync();
        var window = DateTimeOffset.UtcNow.AddDays(10);
        await harness.OpenAsync(HoldId, amount: 100, releaseNotBefore: window);

        var continued = await harness.ReleaseIntoHoldAsync(
            HoldId, NextHoldId, Worker, window.AddDays(5));

        continued.IsError.Should().BeFalse(continued.Message);
        continued.Result!.Hold.State.Should().Be(nameof(HoldState.ReleasedIntoHold));
        continued.Result.NextHold!.HoldId.Should().Be(NextHoldId);
        continued.Result.NextHold.InheritsReserveCommitment.Should().BeTrue();
        harness.Transfers.Should().BeEmpty("continuing a hold moves nothing on chain");

        var counters = await harness.CountersAsync();
        counters.Held.Should().Be(new BigInteger(100), "one commitment, still committed once");
    }

    [Fact]
    public async Task ReleaseIntoHoldAsync_refuses_a_shorter_window_than_the_hold_it_continues()
    {
        var harness = await HoldHarness.ReadyAsync();
        var window = DateTimeOffset.UtcNow.AddDays(30);
        await harness.OpenAsync(HoldId, amount: 100, releaseNotBefore: window);

        var continued = await harness.ReleaseIntoHoldAsync(
            HoldId, NextHoldId, Worker, window.AddDays(-1));

        continued.IsError.Should().BeTrue(
            "a chain of shorter holds would be the way out of the funder's window");
    }

    [Fact]
    public async Task ReleaseIntoHoldAsync_refuses_a_continuation_with_no_window_at_all()
    {
        var harness = await HoldHarness.ReadyAsync();
        await harness.OpenAsync(HoldId, amount: 100, releaseNotBefore: DateTimeOffset.UtcNow.AddDays(30));

        var continued = await harness.ReleaseIntoHoldAsync(HoldId, NextHoldId, Worker, releaseNotBefore: null);

        continued.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Releasing_a_continued_hold_draws_the_partition_exactly_once()
    {
        var harness = await HoldHarness.ReadyAsync();
        var past = DateTimeOffset.UtcNow.AddSeconds(-1);
        await harness.OpenAsync(HoldId, amount: 100, releaseNotBefore: past);
        await harness.ReleaseIntoHoldAsync(HoldId, NextHoldId, Worker, past);

        var released = await harness.ReleaseAsync(NextHoldId);

        released.IsError.Should().BeFalse(released.Message);
        harness.Transfers.Should().ContainSingle();
        harness.Transfers[0].To.Should().Be(harness.WorkerAddress, "the continuing hold names a new beneficiary");

        var counters = await harness.CountersAsync();
        counters.Held.Should().Be(BigInteger.Zero);
        counters.Issued.Should().Be(new BigInteger(100),
            "the parent drew the commitment; the child must not draw it a second time");
    }

    [Fact]
    public async Task Refunding_a_continued_hold_cancels_the_chains_single_commitment_exactly_once()
    {
        var harness = await HoldHarness.ReadyAsync();
        var past = DateTimeOffset.UtcNow.AddSeconds(-1);
        await harness.OpenAsync(HoldId, amount: 100, releaseNotBefore: past);
        await harness.ReleaseIntoHoldAsync(HoldId, NextHoldId, Worker, past);

        var refunded = await harness.RefundAsync(NextHoldId);

        refunded.IsError.Should().BeFalse(refunded.Message);

        var counters = await harness.CountersAsync();
        counters.Held.Should().Be(BigInteger.Zero, "nobody is going to be paid, so nothing stays committed");
        counters.Available.Should().Be(new BigInteger(1000));
    }

    [Fact]
    public async Task A_hold_that_has_been_continued_can_no_longer_be_settled_itself()
    {
        var harness = await HoldHarness.ReadyAsync();
        var past = DateTimeOffset.UtcNow.AddSeconds(-1);
        await harness.OpenAsync(HoldId, amount: 100, releaseNotBefore: past);
        await harness.ReleaseIntoHoldAsync(HoldId, NextHoldId, Worker, past);

        var refundedParent = await harness.RefundAsync(HoldId);
        var releasedParent = await harness.ReleaseAsync(HoldId);

        refundedParent.IsError.Should().BeTrue(
            "the commitment moved to the continuing hold; cancelling it here would cancel it twice");
        releasedParent.IsError.Should().BeTrue();
        harness.Transfers.Should().BeEmpty();
    }

    // ── The sweep's scope ─────────────────────────────────────────────────────

    [Fact]
    public async Task ListDueAsync_returns_due_auto_release_holds_and_holds_stuck_mid_release()
    {
        var harness = await HoldHarness.ReadyAsync();
        await harness.OpenAsync("due", amount: 10, releaseNotBefore: DateTimeOffset.UtcNow.AddSeconds(-1));
        await harness.OpenAsync("not-due", amount: 10, releaseNotBefore: DateTimeOffset.UtcNow.AddDays(5));
        await harness.OpenAsync("manual", amount: 10, autoRelease: false);

        // A hold a crash left mid-release is overdue by definition. It carries
        // autoRelease: false so ONLY the stuck-in-Releasing branch can surface it.
        await harness.OpenAsync("stuck", amount: 10, autoRelease: false);
        harness.TransferOutcome = TransferOutcome.Fails;
        await harness.ReleaseAsync("stuck");
        harness.TransferOutcome = TransferOutcome.Succeeds;
        (await harness.StateOfAsync("stuck")).Should().Be(nameof(HoldState.Releasing));

        var due = await harness.Holds.ListDueAsync(Chain, DateTimeOffset.UtcNow);

        due.Result!.Select(row => row.HoldId).Should().BeEquivalentTo(new[] { "due", "stuck" });
    }

    [Fact]
    public void The_sweep_refuses_to_guess_a_chain_when_the_operator_named_none()
    {
        var worker = new HoldSweepWorker(
            new Mock<IServiceScopeFactory>().Object,
            new HoldOptions(),
            new ReserveOptions(),
            new Mock<ILogger<HoldSweepWorker>>().Object);

        worker.ResolveSweepChains().Should().BeEmpty(
            "releasing value on a chain nobody named is exactly the quiet reach this node refuses");
    }

    [Fact]
    public void The_sweep_falls_back_to_the_chains_the_reserve_is_configured_for()
    {
        var worker = new HoldSweepWorker(
            new Mock<IServiceScopeFactory>().Object,
            new HoldOptions(),
            new ReserveOptions { Chains = new List<string> { Chain } },
            new Mock<ILogger<HoldSweepWorker>>().Object);

        worker.ResolveSweepChains().Should().BeEquivalentTo(new[] { Chain });
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private enum TransferOutcome
    {
        Succeeds,
        Fails,
        BroadcastThenTimeout,
    }

    private sealed record TransferRecord(string AssetId, string From, string To, BigInteger Amount);

    private sealed class HoldHarness
    {
        private readonly InMemoryHoldLedgerStore _ledger = new();

        private HoldHarness(string reserveAddress, string beneficiaryAddress)
        {
            ReserveAddress = reserveAddress;
            BeneficiaryAddress = beneficiaryAddress;
        }

        public IPlatformHold Holds { get; private set; } = null!;
        public IPlatformReserve Reserve { get; private set; } = null!;
        public FakeRecipientPreparer Preparer { get; } = new();
        public string ReserveAddress { get; }
        public string BeneficiaryAddress { get; }
        public string WorkerAddress { get; private set; } = string.Empty;
        public List<TransferRecord> Transfers { get; } = new();
        public TransferOutcome TransferOutcome { get; set; } = TransferOutcome.Succeeds;
        public ChainConfirmation Confirmation { get; set; } = ChainConfirmation.Unknown;

        /// <summary>A node with a real reserve, a declared partition, and wallets for both people.</summary>
        public static async Task<HoldHarness> ReadyAsync(
            long declared = 1000, Guid? assetTenantId = null)
        {
            var platformAccount = new AlgorandAccount();
            var platformKey = (byte[])platformAccount.KeyPair.ClearTextPrivateKey.Clone();
            var beneficiaryAccount = new AlgorandAccount();
            var workerAccount = new AlgorandAccount();

            var harness = new HoldHarness(
                platformAccount.Address.ToString(), beneficiaryAccount.Address.ToString())
            {
                WorkerAddress = workerAccount.Address.ToString(),
            };

            var reserveOptions = new ReserveOptions();
            var factory = new Mock<IBlockchainProviderFactory>();
            factory.SetupGet(f => f.DefaultNetwork).Returns(ChainNetwork.Devnet);

            var provider = new Mock<IBlockchainProvider>();
            provider
                .Setup(p => p.TransferAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<RailAmount>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string asset, string from, string to, RailAmount amount,
                        SigningContext? _, CancellationToken __) =>
                {
                    if (harness.TransferOutcome == TransferOutcome.Fails)
                        return AZOAResult<string>.Failure("the rail refused the payment");

                    harness.Transfers.Add(new TransferRecord(asset, from, to, amount.BaseUnits));
                    var reference = "TX-" + harness.Transfers.Count;

                    // A broadcast-then-timeout still hands back the rail's reference; that
                    // is what the retry has to reconcile against instead of re-sending.
                    return harness.TransferOutcome == TransferOutcome.BroadcastThenTimeout
                        ? AZOAResult<string>.Failure(
                            "the payment was submitted but its result was not read", reference)
                        : AZOAResult<string>.Success(reference);
                });
            provider
                .Setup(p => p.GetTransactionConfirmationAsync(
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<ChainConfirmation>.Success(harness.Confirmation));
            factory
                .Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(provider.Object);

            harness.Reserve = new PlatformReserveService(
                new FakeKeyCustodyService(platformKey),
                factory.Object,
                new InMemoryPlatformReserveLedgerStore(),
                reserveOptions);

            await harness.Reserve.RegisterAssetAsync(new ReserveAssetRegistration(
                Chain, AssetId, "STAKE", 0, new BigInteger(1_000_000), assetTenantId));
            await harness.Reserve.DeclarePartitionsAsync(
                Chain, AssetId,
                new Dictionary<string, BigInteger> { [Partition] = new(declared) },
                assetTenantId is { } assetOwner && assetOwner != Guid.Empty
                    ? ReserveDrawAuthority.ForTenant(assetOwner)
                    : ReserveDrawAuthority.NodeOperator);

            var wallets = new FakeWalletStore();
            wallets.Add(Beneficiary, Chain, beneficiaryAccount.Address.ToString());
            wallets.Add(Worker, Chain, workerAccount.Address.ToString());

            harness.Holds = new PlatformHoldService(
                harness.Reserve,
                harness.Preparer,
                factory.Object,
                wallets,
                harness._ledger,
                reserveOptions,
                new Mock<ILogger<PlatformHoldService>>().Object);

            return harness;
        }

        /// <summary>
        /// Opens a hold on the node's OWN rail unless a test says otherwise: the harness
        /// asset is untenanted by default, and the reserve refuses a draw on the node's own
        /// unit that carries neither an operator signal nor a resolvable tenant (OI-21).
        /// That refusal is proved on purpose in AllocationReserveTenantScopeTests and
        /// PlatformReserveTests; these fixtures are about the hold lifecycle.
        /// </summary>
        public Task<AZOAResult<HoldOutcome>> OpenAsync(
            string holdId,
            long amount,
            string assetId = AssetId,
            DateTimeOffset? releaseNotBefore = null,
            bool autoRelease = true,
            Guid? actingTenantId = null,
            ReserveDrawAuthority? drawAuthority = null) =>
            Holds.OpenAsync(new HoldOpenRequest(
                new HoldReference(Chain, holdId),
                assetId,
                new BigInteger(amount),
                HoldFundingSource.Reserve,
                Beneficiary,
                ReservePartition: Partition,
                FunderUserId: Guid.Empty,
                ReleaseNotBefore: releaseNotBefore,
                AutoRelease: autoRelease,
                ActingTenantId: actingTenantId,
                Purpose: null,
                DrawAuthority: drawAuthority
                    ?? (actingTenantId is null ? ReserveDrawAuthority.NodeOperator : null)));

        public Task<AZOAResult<HoldOutcome>> ReleaseAsync(string holdId) =>
            Holds.ReleaseAsync(new HoldSettlementRequest(new HoldReference(Chain, holdId)));

        public Task<AZOAResult<HoldOutcome>> RefundAsync(string holdId) =>
            Holds.RefundAsync(new HoldSettlementRequest(new HoldReference(Chain, holdId)));

        public Task<AZOAResult<HoldOutcome>> ReleaseIntoHoldAsync(
            string holdId, string nextHoldId, Guid nextBeneficiary, DateTimeOffset? releaseNotBefore) =>
            Holds.ReleaseIntoHoldAsync(new HoldChainRequest(
                new HoldReference(Chain, holdId), nextHoldId, nextBeneficiary, releaseNotBefore));

        public async Task<string> StateOfAsync(string holdId)
        {
            var row = await Holds.GetAsync(new HoldReference(Chain, holdId));
            row.IsError.Should().BeFalse(row.Message);
            return row.Result!.State;
        }

        public async Task<ReservePartitionCounters> CountersAsync()
        {
            var row = await Reserve.GetLedgerAsync(Chain, AssetId);
            row.IsError.Should().BeFalse(row.Message);

            var partitions = ReservePartitionCodec.Decode(row.Result!.Partitions, out var error);
            error.Should().BeEmpty();
            return partitions!.TryGetValue(Partition, out var counters)
                ? counters
                : ReservePartitionCounters.Declaring(BigInteger.Zero);
        }
    }

    /// <summary>Records every preparation and can be made to refuse or to stay pending.</summary>
    private sealed class FakeRecipientPreparer : ICustodialRecipientPreparer
    {
        public List<CustodialRecipientPreparation> Calls { get; } = new();

        public bool PendingConfirmation { get; set; }

        public bool Refuses { get; set; }

        public Task<AZOAResult<CustodialRecipientReadiness>> PrepareAsync(
            CustodialRecipientPreparation request, CancellationToken ct = default)
        {
            Calls.Add(request);

            if (Refuses)
                return Task.FromResult(AZOAResult<CustodialRecipientReadiness>.Failure(
                    "the recipient could not be prepared"));

            return Task.FromResult(AZOAResult<CustodialRecipientReadiness>.Success(
                new CustodialRecipientReadiness(
                    AlreadyPrepared: !PendingConfirmation,
                    TransactionId: null,
                    PendingConfirmation: PendingConfirmation,
                    FundedBaseUnits: "0")));
        }
    }

    /// <summary>Just enough wallet store to resolve a person's account on a chain.</summary>
    private sealed class FakeWalletStore : IWalletStore
    {
        private readonly List<IWallet> _wallets = new();

        public void Add(Guid userId, string chainType, string address) =>
            _wallets.Add(new Wallet
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ChainType = chainType,
                Address = address,
            });

        public Task<AZOAResult<IWallet>> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<IWallet>.Failure("Not exercised by these tests."));

        public Task<AZOAResult<IEnumerable<IWallet>>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<IEnumerable<IWallet>>.Success(_wallets));

        public Task<AZOAResult<IEnumerable<IWallet>>> GetByUserAsync(
            Guid userId, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<IEnumerable<IWallet>>.Success(
                _wallets.Where(wallet => wallet.UserId == userId).ToList()));

        public Task<AZOAResult<IWallet>> UpsertAsync(IWallet wallet, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<IWallet>.Success(wallet));

        public Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<bool>.Success(true));
    }

    /// <summary>
    /// In-memory hold ledger with the same compare-and-swap contract as the Surreal
    /// store: a write whose expected version has moved loses, it does not overwrite.
    /// </summary>
    private sealed class InMemoryHoldLedgerStore : IHoldLedgerStore
    {
        private readonly Dictionary<string, HoldLedger> _rows = new(StringComparer.Ordinal);

        public Task<AZOAResult<HoldLedger?>> GetAsync(string recordId, CancellationToken ct = default) =>
            Task.FromResult(new AZOAResult<HoldLedger?>
            {
                Result = _rows.TryGetValue(recordId, out var row) ? Clone(row) : null,
            });

        public Task<AZOAResult<HoldLedgerClaim>> CreateIfAbsentAsync(
            HoldLedger row, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(row.Id, out var existing))
                return Task.FromResult(AZOAResult<HoldLedgerClaim>.Success(
                    new HoldLedgerClaim(Clone(existing), Created: false)));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<HoldLedgerClaim>.Success(
                new HoldLedgerClaim(Clone(row), Created: true)));
        }

        public Task<AZOAResult<HoldLedger>> UpdateIfVersionAsync(
            HoldLedger row, long expectedVersion, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(row.Id, out var existing) || existing.Version != expectedVersion)
                return Task.FromResult(AZOAResult<HoldLedger>.Failure(
                    $"Hold ledger row '{row.Id}' changed underneath this update."));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<HoldLedger>.Success(Clone(row)));
        }

        public Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListForUserAsync(
            string userId, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<IReadOnlyList<HoldLedger>>.Success(
                _rows.Values
                    .Where(row =>
                        string.Equals(row.BeneficiaryUserId, userId, StringComparison.Ordinal) ||
                        string.Equals(row.FunderUserId, userId, StringComparison.Ordinal))
                    .OrderByDescending(row => row.CreatedAt)
                    .Select(Clone)
                    .ToList()));

        public Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListDueAsync(
            string chain, string network, DateTimeOffset asOf, int limit, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<IReadOnlyList<HoldLedger>>.Success(
                _rows.Values
                    .Where(row =>
                        string.Equals(row.Chain, chain, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(row.Network, network, StringComparison.OrdinalIgnoreCase) &&
                        ((row.State == nameof(HoldState.Held) && row.AutoRelease &&
                          (row.ReleaseNotBefore is null || row.ReleaseNotBefore <= asOf)) ||
                         row.State == nameof(HoldState.Releasing)))
                    .OrderBy(row => row.ReleaseNotBefore ?? DateTimeOffset.MinValue)
                    .Take(limit)
                    .Select(Clone)
                    .ToList()));

        private static HoldLedger Clone(HoldLedger row) => new()
        {
            Id = row.Id,
            HoldId = row.HoldId,
            Chain = row.Chain,
            Network = row.Network,
            HoldingAddress = row.HoldingAddress,
            AssetId = row.AssetId,
            Amount = row.Amount,
            ReservePartition = row.ReservePartition,
            FundingSource = row.FundingSource,
            FunderUserId = row.FunderUserId,
            BeneficiaryUserId = row.BeneficiaryUserId,
            TenantId = row.TenantId,
            Purpose = row.Purpose,
            State = row.State,
            ReleaseNotBefore = row.ReleaseNotBefore,
            AutoRelease = row.AutoRelease,
            InheritsReserveCommitment = row.InheritsReserveCommitment,
            ParentHoldId = row.ParentHoldId,
            ChildHoldId = row.ChildHoldId,
            SettlementTransactionId = row.SettlementTransactionId,
            SettledAt = row.SettledAt,
            SettlementReason = row.SettlementReason,
            Version = row.Version,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
        };
    }

    /// <summary>In-memory reserve ledger with the store's compare-and-swap contract.</summary>
    private sealed class InMemoryPlatformReserveLedgerStore : IPlatformReserveLedgerStore
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
                    $"Platform reserve ledger row '{row.Id}' changed underneath this update."));

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

    /// <summary>Custody double that hands out a real per-chain platform key.</summary>
    private sealed class FakeKeyCustodyService : IKeyCustodyService
    {
        public FakeKeyCustodyService(byte[]? platformKey) => PlatformKey = platformKey;

        public byte[]? PlatformKey { get; set; }

        public async Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(
            string chainType, bool isPlatformContext, SigningContext ctx, Func<byte[], Task<T>> sign)
        {
            if (!isPlatformContext)
                return AZOAResult<T>.Failure("Platform authority was not asserted.");
            if (PlatformKey is null)
                return AZOAResult<T>.Failure($"No platform key is configured for chain '{chainType}'.");

            return AZOAResult<T>.Success(await sign(PlatformKey));
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
}
