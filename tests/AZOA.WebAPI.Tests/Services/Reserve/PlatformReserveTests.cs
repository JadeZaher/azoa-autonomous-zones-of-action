// SPDX-License-Identifier: UNLICENSED
// W3.1 — the platform reserve: address resolution, the named-partition
// oversubscription check, and the mint-into-reserve target on the fungible-token seam.

using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Tests.TestSupport;
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
            Chain, AssetId, ContributorPartition, ReserveDrawKind.Issue, new BigInteger(10), "  ",
            ReserveDrawAuthority.NodeOperator));

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
    public async Task FungibleAdmission_freezes_the_real_reserve_on_every_asset_authority()
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
    public async Task FungibleAdmission_does_not_register_an_asset_before_authoritative_confirmation()
    {
        var harness = ReserveHarness.WithPlatformKey();
        var mint = new FungibleMintHarness(harness);
        var created = await mint.CreateAsync();
        created.IsError.Should().BeFalse(created.Message);
        created.Result!.Operation!.State.Should().Be("AwaitingConsent");
        created.Result.AssetId.Should().BeEmpty();
        (await harness.Reserve.GetLedgerAsync(Chain, AssetId)).IsError.Should().BeTrue();
        mint.VerifyNoProviderEgress();
    }

    [Fact]
    public async Task FungibleCreate_fails_closed_and_broadcasts_nothing_when_no_reserve_resolves()
    {
        var harness = ReserveHarness.WithoutPlatformKey();
        var mint = new FungibleMintHarness(harness);

        var created = await mint.CreateAsync();

        created.IsError.Should().BeTrue();
        mint.AdmissionCount.Should().Be(0);
        mint.VerifyNoProviderEgress();
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

    // ── Draw authority (OI-20 / OI-21, re-opened) ────────────────────────────
    //
    // The first pass asked the ownership question at two of the SEVEN doors that reach
    // these counters, and answered it with "no acting tenant means the operator" — which
    // is exactly the shape a user-driven action run arrives in (ActionNodeStepHandler
    // builds its context with ActingTenantId = null). The question is now asked HERE,
    // where the counter actually moves, so no door can skip it.

    private static readonly Guid TenantOne = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantTwo = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public async Task DrawAsync_refuses_a_caller_with_no_tenant_on_the_nodes_own_unit()
    {
        var harness = await AuthorityHarnessAsync(tenantId: null);

        var draw = await harness.IssueAsync(
            ContributorPartition, 10, "no-tenant-platform-unit", ReserveDrawAuthority.ForTenant(null));

        draw.IsError.Should().BeTrue(
            "a missing acting tenant is not an operator signal; reading it as one hands every "
            + "holder of a value-signing key the whole of this node's own supply");
        draw.Message.Should().Contain("this node's own unit");
        (await harness.CountersAsync(ContributorPartition)).Committed.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task DrawAsync_refuses_a_tenant_on_the_nodes_own_unit()
    {
        var harness = await AuthorityHarnessAsync(tenantId: null);

        var draw = await harness.IssueAsync(
            ContributorPartition, 10, "tenant-platform-unit", ReserveDrawAuthority.ForTenant(TenantOne));

        draw.IsError.Should().BeTrue();
        draw.Message.Should().Contain("this node's own unit");
        (await harness.CountersAsync(ContributorPartition)).Committed.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task DrawAsync_lets_the_node_operator_draw_the_nodes_own_unit()
    {
        var harness = await AuthorityHarnessAsync(tenantId: null);

        var draw = await harness.IssueAsync(
            ContributorPartition, 10, "operator-platform-unit", ReserveDrawAuthority.NodeOperator);

        draw.IsError.Should().BeFalse(draw.Message);
        draw.Result!.Counters.Issued.Should().Be(new BigInteger(10));
    }

    [Fact]
    public async Task DrawAsync_refuses_a_tenant_drawing_a_partition_another_tenant_registered()
    {
        var harness = await AuthorityHarnessAsync(tenantId: TenantOne);

        var draw = await harness.IssueAsync(
            ContributorPartition, 10, "cross-tenant", ReserveDrawAuthority.ForTenant(TenantTwo));

        draw.IsError.Should().BeTrue();
        draw.Message.Should().Contain("another tenant");
        (await harness.CountersAsync(ContributorPartition)).Committed.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task DrawAsync_lets_the_registering_tenant_draw_its_own_partition()
    {
        var harness = await AuthorityHarnessAsync(tenantId: TenantOne);

        var draw = await harness.IssueAsync(
            ContributorPartition, 10, "own-tenant", ReserveDrawAuthority.ForTenant(TenantOne));

        draw.IsError.Should().BeFalse(draw.Message);
    }

    [Fact]
    public async Task DrawAsync_refuses_an_operator_signal_on_a_tenants_asset()
    {
        // The node's own rails (on-ramp, off-ramp, chargeback recovery) never name a
        // tenant's asset. One that did would be a miswired seam, not a privilege.
        var harness = await AuthorityHarnessAsync(tenantId: TenantOne);

        var draw = await harness.IssueAsync(
            ContributorPartition, 10, "operator-on-tenant-asset", ReserveDrawAuthority.NodeOperator);

        draw.IsError.Should().BeTrue();
        (await harness.CountersAsync(ContributorPartition)).Committed.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task DrawAsync_refuses_a_tenantless_caller_on_a_tenants_asset()
    {
        var harness = await AuthorityHarnessAsync(tenantId: TenantOne);

        var draw = await harness.IssueAsync(
            ContributorPartition, 10, "no-tenant-tenant-asset", ReserveDrawAuthority.ForTenant(null));

        draw.IsError.Should().BeTrue();
        draw.Message.Should().Contain("resolvable acting tenant");
        (await harness.CountersAsync(ContributorPartition)).Committed.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task DrawAsync_never_strands_a_settlement_of_an_already_guarded_commitment()
    {
        // A hold's release, cancel and refund draws read chain, asset and partition off the
        // persisted row. Nothing there was named by a caller, and refusing them would
        // strand value the opening draw already committed.
        var harness = await AuthorityHarnessAsync(tenantId: TenantOne);

        var draw = await harness.IssueAsync(
            ContributorPartition, 10, "settlement", ReserveDrawAuthority.SettlingGuardedCommitment);

        draw.IsError.Should().BeFalse(draw.Message);
    }

    // ── Reserve:PlatformUnitDrawPolicy ────────────────────────────────────────
    // The policy is read at the ONE predicate inside DrawAsync, so these assert the
    // counters, not a helper's return value. The blank cases are the shape a Railway
    // env var set to "" produces: ReserveOptions is bound with .Get<T>() (Program.cs),
    // so a present-but-empty key REPLACES the OperatorOnly C# default rather than
    // leaving it in place.

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DrawAsync_treats_a_blank_platform_unit_policy_as_the_narrowest_setting(string blank)
    {
        var harness = await AuthorityHarnessAsync(
            tenantId: null, options => options.PlatformUnitDrawPolicy = blank);

        var draw = await harness.IssueAsync(
            ContributorPartition, 10, $"blank-policy-{blank.Length}", ReserveDrawAuthority.ForTenant(TenantOne));

        draw.IsError.Should().BeTrue(
            "a policy the operator never filled in must narrow the node, not open it to every "
            + "admitted tenant — an unset Railway variable is silence, not consent");
        draw.Message.Should().Contain("operator's to draw from");
        (await harness.CountersAsync(ContributorPartition)).Committed.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task DrawAsync_refuses_a_platform_unit_policy_this_node_does_not_recognise()
    {
        var harness = await AuthorityHarnessAsync(
            tenantId: null, options => options.PlatformUnitDrawPolicy = "AnyAdmitedTenant");

        var draw = await harness.IssueAsync(
            ContributorPartition, 10, "typo-policy", ReserveDrawAuthority.ForTenant(TenantOne));

        draw.IsError.Should().BeTrue("a typo narrows the node rather than opening it");
        draw.Message.Should().Contain("does not recognise");
        (await harness.CountersAsync(ContributorPartition)).Committed.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task DrawAsync_admits_a_tenant_only_when_the_operator_asked_for_the_widest_policy()
    {
        // The counterweight to the blank/typo cases: the refusals above are the policy
        // working, not the predicate refusing everything unconditionally.
        var harness = await AuthorityHarnessAsync(
            tenantId: null,
            options => options.PlatformUnitDrawPolicy = ReservePlatformUnitDrawPolicies.AnyAdmittedTenant);

        var draw = await harness.IssueAsync(
            ContributorPartition, 10, "widest-policy", ReserveDrawAuthority.ForTenant(TenantOne));

        draw.IsError.Should().BeFalse(draw.Message);
        draw.Result!.Counters.Issued.Should().Be(new BigInteger(10));
    }

    [Fact]
    public async Task DrawAsync_still_refuses_a_tenantless_caller_under_the_widest_policy()
    {
        var harness = await AuthorityHarnessAsync(
            tenantId: null,
            options => options.PlatformUnitDrawPolicy = ReservePlatformUnitDrawPolicies.AnyAdmittedTenant);

        var draw = await harness.IssueAsync(
            ContributorPartition, 10, "widest-policy-no-tenant", ReserveDrawAuthority.ForTenant(null));

        draw.IsError.Should().BeTrue("OI-21: no policy admits a caller this node resolved no tenant for");
        (await harness.CountersAsync(ContributorPartition)).Committed.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task DrawAsync_admits_only_the_declared_tenants_under_DeclaredTenants()
    {
        var harness = await AuthorityHarnessAsync(tenantId: null, options =>
        {
            options.PlatformUnitDrawPolicy = ReservePlatformUnitDrawPolicies.DeclaredTenants;
            options.PlatformUnitDrawTenants = new List<string> { TenantOne.ToString() };
        });

        var admitted = await harness.IssueAsync(
            ContributorPartition, 10, "declared-admitted", ReserveDrawAuthority.ForTenant(TenantOne));
        var refused = await harness.IssueAsync(
            ContributorPartition, 10, "declared-refused", ReserveDrawAuthority.ForTenant(TenantTwo));

        admitted.IsError.Should().BeFalse(admitted.Message);
        refused.IsError.Should().BeTrue();
        refused.Message.Should().Contain("Reserve:PlatformUnitDrawTenants");
        (await harness.CountersAsync(ContributorPartition)).Committed.Should().Be(new BigInteger(10));
    }

    private static async Task<ReserveHarness> AuthorityHarnessAsync(
        Guid? tenantId, Action<ReserveOptions>? configure = null)
    {
        var harness = ReserveHarness.WithPlatformKey(configure);
        var registered = await harness.RegisterAsync(totalSupply: 1_000, tenantId);
        registered.IsError.Should().BeFalse(registered.Message);
        var declared = await harness.DeclareAsAsync(
            tenantId is { } assetOwner && assetOwner != Guid.Empty
                ? ReserveDrawAuthority.ForTenant(assetOwner)
                : ReserveDrawAuthority.NodeOperator,
            (ContributorPartition, 400));
        declared.IsError.Should().BeFalse(declared.Message);
        return harness;
    }

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

        public Task<AZOAResult<PlatformReserveLedger>> RegisterAsync(long totalSupply, Guid? tenantId = null) =>
            Reserve.RegisterAssetAsync(new ReserveAssetRegistration(
                Chain, AssetId, "UNIT", 2, new BigInteger(totalSupply), tenantId));

        public Task<AZOAResult<PlatformReserveLedger>> DeclareAsync(
            params (string Name, long Declared)[] partitions) =>
            DeclareAsAsync(ReserveDrawAuthority.NodeOperator, partitions);

        public Task<AZOAResult<PlatformReserveLedger>> DeclareAsAsync(
            ReserveDrawAuthority authority, params (string Name, long Declared)[] partitions) =>
            Reserve.DeclarePartitionsAsync(
                Chain,
                AssetId,
                partitions.ToDictionary(p => p.Name, p => new BigInteger(p.Declared)),
                authority);

        public Task<AZOAResult<ReserveDrawOutcome>> IssueAsync(
            string partition, long amount, string operationKey, ReserveDrawAuthority? authority = null) =>
            DrawAsync(partition, ReserveDrawKind.Issue, amount, operationKey, authority);

        public Task<AZOAResult<ReserveDrawOutcome>> DrawAsync(
            string partition, ReserveDrawKind kind, long amount, string operationKey,
            ReserveDrawAuthority? authority = null) =>
            Reserve.DrawAsync(new ReserveDrawRequest(
                Chain, AssetId, partition, kind, new BigInteger(amount), operationKey,
                authority ?? ReserveDrawAuthority.NodeOperator));

        public async Task<ReservePartitionCounters> CountersAsync(string partition)
        {
            var row = await Reserve.GetLedgerAsync(Chain, AssetId);
            row.IsError.Should().BeFalse(row.Message);
            var partitions = ReservePartitionCodec.Decode(row.Result!.Partitions, out var error);
            error.Should().BeEmpty();
            return partitions![ReservePartitionCodec.Normalize(partition)];
        }
    }

    private sealed class FungibleMintHarness
    {
        public const string CallerWalletAddress = "caller-wallet";
        public const ulong TotalSupply = 1_000_000;
        private readonly FungibleAdmissionHarness _admission;
        public FungibleMintHarness(ReserveHarness harness)
            => _admission = new(harness.Options.FungibleMintTarget, reserve: harness.Reserve);
        private FungibleCreationIntent? Intent => _admission.Commands.Values.SingleOrDefault()?.Intent;
        public int AdmissionCount => _admission.Commands.Count;
        public string? CapturedManagerAddress => Intent?.ManagerAddress;
        public string? CapturedReserveAddress => Intent?.ReserveAddress;
        public string? CapturedFreezeAddress => Intent?.FreezeAddress;
        public string? CapturedClawbackAddress => Intent?.ClawbackAddress;
        public string? CapturedCreatorAddress => Intent?.CreatorAddress;
        public Task<AZOAResult<FungibleTokenResult>> CreateAsync() => _admission.AdmitAsync(_admission.Request(TotalSupply));
        public void VerifyNoProviderEgress() => _admission.Module.VerifyNoOtherCalls();
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
