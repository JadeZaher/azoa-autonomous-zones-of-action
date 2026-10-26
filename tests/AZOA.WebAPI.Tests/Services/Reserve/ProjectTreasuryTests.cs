// SPDX-License-Identifier: UNLICENSED
// W3.7 — per-project custodial treasuries: the account a project's funding actually
// lands in, its derivation, the credited/spent/held ledger, and close.

using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;
using AlgorandAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Services.Reserve;

public class ProjectTreasuryTests
{
    private const string Chain = "Algorand";
    private const string UnsupportedChain = "Solana";
    private const string AssetId = "1001";
    private const string ProjectAlpha = "project-alpha";
    private const string ProjectBeta = "project-beta";

    private static readonly Guid TenantOne = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantTwo = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OperatorUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ── The account itself ────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_gives_the_project_its_own_account_not_the_platform_reserve()
    {
        var harness = TreasuryHarness.WithPlatformKey();

        var account = await harness.ResolveAsync(TenantOne, ProjectAlpha);

        account.IsError.Should().BeFalse(account.Message);
        account.Result!.Address.Should().NotBeNullOrWhiteSpace();
        account.Result.Address.Should().NotBe(
            harness.ReserveAddress,
            "funding a project must move value TO the project, not to the operator's pooled reserve");
        account.Result.Status.Should().Be(ProjectTreasuryStatus.Open);
        account.Result.DerivationVersion.Should().Be(ProjectTreasuryKeyDerivation.Version);
    }

    [Fact]
    public async Task ResolveAsync_gives_two_projects_two_different_accounts()
    {
        var harness = TreasuryHarness.WithPlatformKey();

        var alpha = await harness.ResolveAsync(TenantOne, ProjectAlpha);
        var beta = await harness.ResolveAsync(TenantOne, ProjectBeta);

        alpha.Result!.Address.Should().NotBe(
            beta.Result!.Address, "two projects sharing one address would pool their money");
    }

    [Fact]
    public async Task ResolveAsync_gives_two_tenants_two_different_accounts_for_the_same_project_id()
    {
        var harness = TreasuryHarness.WithPlatformKey();

        var one = await harness.ResolveAsync(TenantOne, ProjectAlpha);
        var two = await harness.ResolveAsync(TenantTwo, ProjectAlpha);

        one.Result!.Address.Should().NotBe(
            two.Result!.Address, "a project id is only unique inside its own tenant");
    }

    [Fact]
    public async Task ResolveAsync_is_idempotent_and_never_creates_a_second_account()
    {
        var harness = TreasuryHarness.WithPlatformKey();

        var first = await harness.ResolveAsync(TenantOne, ProjectAlpha);
        var second = await harness.ResolveAsync(TenantOne, ProjectAlpha);

        second.IsError.Should().BeFalse(second.Message);
        second.Result!.Address.Should().Be(first.Result!.Address);
        harness.Ledger.Count.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_fails_closed_when_the_chain_has_no_platform_key()
    {
        var harness = TreasuryHarness.WithoutPlatformKey();

        var account = await harness.ResolveAsync(TenantOne, ProjectAlpha);

        account.IsError.Should().BeTrue();
        account.Result.Should().BeNull();
        account.Message.Should().Contain(Chain);
        harness.Ledger.Count.Should().Be(0, "no row may exist for an account this node cannot sign for");
    }

    [Fact]
    public async Task ResolveAsync_refuses_a_chain_the_operator_never_approved()
    {
        var harness = TreasuryHarness.WithPlatformKey(options =>
            options.Chains = new List<string> { UnsupportedChain });

        var account = await harness.ResolveAsync(TenantOne, ProjectAlpha);

        account.IsError.Should().BeTrue();
        account.Message.Should().Contain("Reserve:Chains");
    }

    [Fact]
    public async Task ResolveAsync_refuses_a_chain_with_no_treasury_derivation_rule()
    {
        var harness = TreasuryHarness.WithPlatformKey();

        var account = await harness.Treasury.ResolveAsync(
            new ProjectTreasuryReference(UnsupportedChain, TenantOne, ProjectAlpha));

        account.IsError.Should().BeTrue("a chain with no rule must fail closed, not guess a key shape");
        account.Message.Should().Contain(UnsupportedChain);
    }

    [Fact]
    public async Task ResolveAsync_refuses_an_incomplete_reference()
    {
        var harness = TreasuryHarness.WithPlatformKey();

        var blankProject = await harness.Treasury.ResolveAsync(
            new ProjectTreasuryReference(Chain, TenantOne, "   "));
        var noTenant = await harness.Treasury.ResolveAsync(
            new ProjectTreasuryReference(Chain, Guid.Empty, ProjectAlpha));

        blankProject.IsError.Should().BeTrue();
        noTenant.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_fails_closed_when_the_platform_key_rotated_under_a_recorded_treasury()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        var original = await harness.ResolveAsync(TenantOne, ProjectAlpha);
        original.IsError.Should().BeFalse(original.Message);

        harness.RotatePlatformKey();

        var afterRotation = await harness.ResolveAsync(TenantOne, ProjectAlpha);

        afterRotation.IsError.Should().BeTrue(
            "a rotated platform key derives a DIFFERENT account, which holds none of this project's value");
        afterRotation.Message.Should().Contain("platform key");
    }

    // ── Signing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SignAsTreasuryAsync_hands_over_the_project_key_and_never_the_platform_key()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        var account = await harness.ResolveAsync(TenantOne, ProjectAlpha);
        byte[]? handed = null;

        var signed = await harness.Treasury.SignAsTreasuryAsync(
            new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha),
            SigningContext.PlatformFor(Chain),
            key =>
            {
                handed = (byte[])key.Clone();
                return Task.FromResult("signed");
            });

        signed.IsError.Should().BeFalse(signed.Message);
        signed.Result.Should().Be("signed");
        handed.Should().NotBeNull();
        handed!.SequenceEqual(harness.PlatformKey!).Should().BeFalse(
            "the treasury signs as itself, not as the reserve");
        new AlgorandAccount(handed).Address.ToString().Should().Be(
            account.Result!.Address,
            "the key handed to the signer must control exactly the address the treasury advertises");
    }

    [Fact]
    public async Task SignAsTreasuryAsync_zeroes_the_derived_key_after_the_callback_returns()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        byte[]? liveBuffer = null;

        await harness.Treasury.SignAsTreasuryAsync(
            new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha),
            SigningContext.PlatformFor(Chain),
            key =>
            {
                liveBuffer = key;
                return Task.FromResult(0);
            });

        liveBuffer.Should().NotBeNull();
        liveBuffer!.Should().OnlyContain(b => b == 0, "a derived signing key must not outlive its use");
    }

    [Fact]
    public async Task SignAsTreasuryAsync_refuses_a_per_user_signing_context()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);

        var signed = await harness.Treasury.SignAsTreasuryAsync(
            new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha),
            SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()),
            _ => Task.FromResult("signed"));

        signed.IsError.Should().BeTrue("a user context must never be able to spend a project's treasury");
    }

    [Fact]
    public async Task SignAsTreasuryAsync_refuses_a_treasury_nobody_resolved()
    {
        var harness = TreasuryHarness.WithPlatformKey();

        var signed = await harness.Treasury.SignAsTreasuryAsync(
            new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha),
            SigningContext.PlatformFor(Chain),
            _ => Task.FromResult("signed"));

        signed.IsError.Should().BeTrue("the node must not sign for an account it has no record of");
    }

    // ── Balance reads ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBalanceAsync_reads_the_projects_own_address()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        var account = await harness.ResolveAsync(TenantOne, ProjectAlpha);
        harness.ProviderBalance = "4200";

        var balance = await harness.Treasury.GetBalanceAsync(
            new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha), AssetId);

        balance.IsError.Should().BeFalse(balance.Message);
        balance.Result.Should().Be("4200");
        harness.LastBalanceAddress.Should().Be(account.Result!.Address);
    }

    // ── The credited / spent / held ledger ────────────────────────────────────

    [Fact]
    public async Task Credit_then_spend_walks_availability_down()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);

        await harness.MoveAsync(ProjectTreasuryMovementKind.Credit, 1_000, "op-credit");
        var spend = await harness.MoveAsync(ProjectTreasuryMovementKind.Spend, 400, "op-spend");

        spend.IsError.Should().BeFalse(spend.Message);
        var counters = await harness.CountersAsync();
        counters.Credited.Should().Be(new BigInteger(1_000));
        counters.Spent.Should().Be(new BigInteger(400));
        counters.Available.Should().Be(new BigInteger(600));
    }

    [Fact]
    public async Task RecordMovementAsync_refuses_a_spend_beyond_availability()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        await harness.MoveAsync(ProjectTreasuryMovementKind.Credit, 100, "op-credit");

        var spend = await harness.MoveAsync(ProjectTreasuryMovementKind.Spend, 101, "op-spend");

        spend.IsError.Should().BeTrue();
        spend.Message.Should().Contain("available");
        (await harness.CountersAsync()).Spent.Should().Be(BigInteger.Zero, "a refused spend moves nothing");
    }

    [Fact]
    public async Task RecordMovementAsync_refuses_a_spend_on_an_asset_the_treasury_never_held()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);

        var spend = await harness.MoveAsync(ProjectTreasuryMovementKind.Spend, 1, "op-spend");

        spend.IsError.Should().BeTrue("an unknown asset starts at zero availability, not at unlimited");
    }

    [Fact]
    public async Task A_hold_reserves_availability_and_releasing_it_settles_as_spent()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        await harness.MoveAsync(ProjectTreasuryMovementKind.Credit, 1_000, "op-credit");

        await harness.MoveAsync(ProjectTreasuryMovementKind.Hold, 300, "op-hold");
        var held = await harness.CountersAsync();
        held.Held.Should().Be(new BigInteger(300));
        held.Available.Should().Be(new BigInteger(700));

        await harness.MoveAsync(ProjectTreasuryMovementKind.ReleaseHold, 300, "op-release");
        var settled = await harness.CountersAsync();
        settled.Held.Should().Be(BigInteger.Zero);
        settled.Spent.Should().Be(new BigInteger(300));
        settled.Available.Should().Be(new BigInteger(700), "releasing a hold is net-zero against availability");
    }

    [Fact]
    public async Task Cancelling_a_hold_returns_the_units_to_availability()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        await harness.MoveAsync(ProjectTreasuryMovementKind.Credit, 1_000, "op-credit");
        await harness.MoveAsync(ProjectTreasuryMovementKind.Hold, 300, "op-hold");

        await harness.MoveAsync(ProjectTreasuryMovementKind.CancelHold, 300, "op-cancel");

        var counters = await harness.CountersAsync();
        counters.Held.Should().Be(BigInteger.Zero);
        counters.Spent.Should().Be(BigInteger.Zero);
        counters.Available.Should().Be(new BigInteger(1_000));
    }

    [Fact]
    public async Task RecordMovementAsync_refuses_releasing_more_than_is_held()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        await harness.MoveAsync(ProjectTreasuryMovementKind.Credit, 1_000, "op-credit");
        await harness.MoveAsync(ProjectTreasuryMovementKind.Hold, 100, "op-hold");

        var release = await harness.MoveAsync(ProjectTreasuryMovementKind.ReleaseHold, 200, "op-release");

        release.IsError.Should().BeTrue();
        release.Message.Should().Contain("holds only");
    }

    [Fact]
    public async Task RecordMovementAsync_replays_a_duplicate_operation_key_without_moving_anything()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        await harness.MoveAsync(ProjectTreasuryMovementKind.Credit, 1_000, "op-credit");

        var retried = await harness.MoveAsync(ProjectTreasuryMovementKind.Credit, 1_000, "op-credit");

        retried.IsError.Should().BeFalse(retried.Message);
        retried.Result!.Replayed.Should().BeTrue();
        (await harness.CountersAsync()).Credited.Should().Be(
            new BigInteger(1_000), "a retried credit must not double the project's money");
    }

    [Fact]
    public async Task RecordMovementAsync_requires_an_operation_key()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);

        var moved = await harness.MoveAsync(ProjectTreasuryMovementKind.Credit, 10, "  ");

        moved.IsError.Should().BeTrue();
        moved.Message.Should().Contain("operation key");
    }

    [Fact]
    public async Task RecordMovementAsync_refuses_a_treasury_nobody_resolved()
    {
        var harness = TreasuryHarness.WithPlatformKey();

        var moved = await harness.MoveAsync(ProjectTreasuryMovementKind.Credit, 10, "op-credit");

        moved.IsError.Should().BeTrue("a movement must never bring an account into existence as a side effect");
        harness.Ledger.Count.Should().Be(0);
    }

    [Fact]
    public async Task An_unreadable_assets_blob_is_an_error_not_an_empty_treasury()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        harness.Ledger.CorruptAssets();

        var moved = await harness.MoveAsync(ProjectTreasuryMovementKind.Spend, 1, "op-spend");

        moved.IsError.Should().BeTrue("reading unreadable counters as zero would hand out money nobody has");
    }

    // ── Close ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CloseAsync_refuses_while_the_project_still_has_units()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        await harness.MoveAsync(ProjectTreasuryMovementKind.Credit, 500, "op-credit");

        var closed = await harness.CloseAsync("project retired");

        closed.IsError.Should().BeTrue("closing over live value would strand the project's money");
        closed.Message.Should().Contain(AssetId);
    }

    [Fact]
    public async Task CloseAsync_refuses_while_units_are_held()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        await harness.MoveAsync(ProjectTreasuryMovementKind.Credit, 500, "op-credit");
        await harness.MoveAsync(ProjectTreasuryMovementKind.Hold, 500, "op-hold");

        var closed = await harness.CloseAsync("project retired");

        closed.IsError.Should().BeTrue("a held unit is promised to a payout that has not settled");
    }

    [Fact]
    public async Task CloseAsync_succeeds_once_every_asset_is_settled_and_then_refuses_movements()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        await harness.MoveAsync(ProjectTreasuryMovementKind.Credit, 500, "op-credit");
        await harness.MoveAsync(ProjectTreasuryMovementKind.Spend, 500, "op-sweep");

        var closed = await harness.CloseAsync("project retired");

        closed.IsError.Should().BeFalse(closed.Message);
        closed.Result!.Status.Should().Be(ProjectTreasuryStatus.Closed.ToString());
        closed.Result.CloseReason.Should().Be("project retired");
        closed.Result.ClosedByUserId.Should().Be($"user:{OperatorUserId:N}");

        var afterClose = await harness.MoveAsync(ProjectTreasuryMovementKind.Credit, 1, "op-late-credit");
        afterClose.IsError.Should().BeTrue("a retired treasury must not accept new value");
    }

    [Fact]
    public async Task CloseAsync_requires_a_reason_and_an_operator()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        var reference = new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha);

        var noReason = await harness.Treasury.CloseAsync(
            new ProjectTreasuryCloseRequest(reference, "  ", OperatorUserId));
        var noOperator = await harness.Treasury.CloseAsync(
            new ProjectTreasuryCloseRequest(reference, "retired", Guid.Empty));

        noReason.IsError.Should().BeTrue();
        noOperator.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task CloseAsync_is_idempotent()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        await harness.CloseAsync("project retired");

        var again = await harness.CloseAsync("project retired again");

        again.IsError.Should().BeFalse(again.Message);
        again.Result!.CloseReason.Should().Be("project retired", "the first close is the attributable one");
    }

    // ── Operator listing ──────────────────────────────────────────────────────

    [Fact]
    public async Task ListByTenantAsync_returns_only_that_tenants_treasuries()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        await harness.ResolveAsync(TenantOne, ProjectBeta);
        await harness.ResolveAsync(TenantTwo, ProjectAlpha);

        var listed = await harness.Treasury.ListByTenantAsync(TenantOne);

        listed.IsError.Should().BeFalse(listed.Message);
        listed.Result!.Should().HaveCount(2);
        listed.Result.Should().OnlyContain(row => row.TenantId == $"user:{TenantOne:N}");
    }

    [Fact]
    public async Task ListByTenantAsync_caps_the_page_size()
    {
        var harness = TreasuryHarness.WithPlatformKey();
        await harness.ResolveAsync(TenantOne, ProjectAlpha);
        await harness.ResolveAsync(TenantOne, ProjectBeta);

        var listed = await harness.Treasury.ListByTenantAsync(TenantOne, limit: 1);

        listed.Result!.Should().HaveCount(1);
    }

    // ── The derivation itself ─────────────────────────────────────────────────

    [Fact]
    public void Derive_binds_the_key_to_chain_tenant_and_project()
    {
        var platformKey = RandomNumberGenerator.GetBytes(AlgorandPlatformKeyDerivation.PrivateKeyLength);

        var alpha = ProjectTreasuryKeyDerivation.Derive(
            Chain, platformKey, new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha));
        var alphaAgain = ProjectTreasuryKeyDerivation.Derive(
            Chain, platformKey, new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha));
        var beta = ProjectTreasuryKeyDerivation.Derive(
            Chain, platformKey, new ProjectTreasuryReference(Chain, TenantOne, ProjectBeta));

        alpha.Should().NotBeNull();
        alpha!.Length.Should().Be(AlgorandPlatformKeyDerivation.PrivateKeyLength);
        alpha.SequenceEqual(alphaAgain!).Should().BeTrue(
            "the derivation must be reproducible from configuration alone");
        alpha.SequenceEqual(beta!).Should().BeFalse(
            "two projects sharing one key would share one account");
        alpha.SequenceEqual(platformKey).Should().BeFalse(
            "a project key is a CHILD of the platform key, never the same key");
    }

    [Fact]
    public void Derive_fails_closed_for_a_chain_with_no_rule()
    {
        var platformKey = RandomNumberGenerator.GetBytes(AlgorandPlatformKeyDerivation.PrivateKeyLength);

        ProjectTreasuryKeyDerivation.IsSupported(UnsupportedChain).Should().BeFalse();
        ProjectTreasuryKeyDerivation
            .Derive(UnsupportedChain, platformKey, new ProjectTreasuryReference(UnsupportedChain, TenantOne, ProjectAlpha))
            .Should().BeNull();
    }

    // ── Simulated chain, the dev node's real composition ──────────────────────

    [Fact]
    public async Task ResolveAsync_on_Simulated_derives_a_stable_per_project_sim_address_from_the_dev_config()
    {
        var (treasury, ledger, config, options) = TreasuryFromShippedConfig(developmentOverlay: true);
        var simulatedProvider = new AZOA.WebAPI.Providers.Blockchain.Simulated.SimulatedBlockchainProvider(config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AZOA.WebAPI.Providers.Blockchain.Simulated.SimulatedBlockchainProvider>.Instance);

        var alpha = await treasury.ResolveAsync(new ProjectTreasuryReference(SimulatedChain, TenantOne, ProjectAlpha));
        var alphaAgain = await treasury.ResolveAsync(new ProjectTreasuryReference(SimulatedChain, TenantOne, ProjectAlpha));
        var beta = await treasury.ResolveAsync(new ProjectTreasuryReference(SimulatedChain, TenantOne, ProjectBeta));

        alpha.IsError.Should().BeFalse(alpha.Message);
        alpha.Result!.Address.Should().StartWith("sim:simulated:");
        (await simulatedProvider.ValidateAddressAsync(alpha.Result.Address)).Result.Should().BeTrue();
        alpha.Result.Address.Should().NotBe(options.ExpectedAddresses[SimulatedChain],
            "a project treasury is the project's own account, never the platform reserve");
        alphaAgain.Result!.Address.Should().Be(alpha.Result.Address, "resolve is idempotent");
        beta.Result!.Address.Should().NotBe(alpha.Result.Address, "two projects never share one account");
        ledger.Count.Should().Be(2);
    }

    [Fact]
    public async Task ResolveAsync_on_Simulated_refuses_under_the_shipped_base_config_alone()
    {
        var (treasury, ledger, _, _) = TreasuryFromShippedConfig(developmentOverlay: false);

        var account = await treasury.ResolveAsync(new ProjectTreasuryReference(SimulatedChain, TenantOne, ProjectAlpha));

        account.IsError.Should().BeTrue();
        account.Message.Should().Contain("Reserve:Chains");
        ledger.Count.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_on_Algorand_still_refuses_without_a_mnemonic_under_the_dev_config()
    {
        var (treasury, ledger, _, _) = TreasuryFromShippedConfig(developmentOverlay: true);

        var account = await treasury.ResolveAsync(new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha));

        account.IsError.Should().BeTrue();
        account.Message.Should().Contain(AlgorandPlatformKeyDerivation.ConfigPath);
        ledger.Count.Should().Be(0);
    }

    private const string SimulatedChain = WalletKeyService.SimulatedChainType;

    /// <summary>Shipped appsettings (+ optional Development overlay) with REAL custody, as PlatformReserveTests does.</summary>
    private static (ProjectTreasuryService Treasury, InMemoryProjectTreasuryLedgerStore Ledger, IConfiguration Config, ReserveOptions Options)
        TreasuryFromShippedConfig(bool developmentOverlay)
    {
        var builder = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AZOA:WalletEncryptionKey"] = "test-encryption-key-for-unit-tests-min-32-chars!!" });
        if (developmentOverlay) builder.AddJsonFile("appsettings.Development.json", optional: false);
        var config = builder.Build();
        var options = config.GetSection(ReserveOptions.SectionName).Get<ReserveOptions>()!;
        var consent = new Mock<ITenantConsentGate>();
        consent.Setup(gate => gate.EnsureAllowedAsync(It.IsAny<SigningContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Success(true));
        var custody = new KeyCustodyService(
            Mock.Of<AZOA.WebAPI.Interfaces.Stores.IWalletStore>(), new WalletKeyService(config), config, consent.Object);
        var factory = new Mock<IBlockchainProviderFactory>();
        factory.SetupGet(f => f.DefaultNetwork).Returns(ChainNetwork.Devnet);
        var ledger = new InMemoryProjectTreasuryLedgerStore();
        return (new ProjectTreasuryService(custody, factory.Object, ledger, options), ledger, config, options);
    }

    [Fact]
    public void Asset_ids_keep_their_case_so_two_base58_mints_never_collide()
    {
        ProjectTreasuryAssetCodec.Normalize("  AbC  ").Should().Be("AbC");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class TreasuryHarness
    {
        private TreasuryHarness(
            InMemoryProjectTreasuryLedgerStore ledger,
            FakeKeyCustodyService custody,
            ReserveOptions options,
            string reserveAddress)
        {
            Ledger = ledger;
            Custody = custody;
            Options = options;
            ReserveAddress = reserveAddress;
        }

        public ProjectTreasuryService Treasury { get; private set; } = null!;
        public InMemoryProjectTreasuryLedgerStore Ledger { get; }
        public FakeKeyCustodyService Custody { get; }
        public ReserveOptions Options { get; }

        /// <summary>The address the PLATFORM RESERVE resolves — what a project must never share.</summary>
        public string ReserveAddress { get; }

        public byte[]? PlatformKey => Custody.PlatformKey;

        public string ProviderBalance { get; set; } = "0";
        public string? LastBalanceAddress { get; private set; }

        public static TreasuryHarness WithPlatformKey(Action<ReserveOptions>? configure = null)
        {
            var account = new AlgorandAccount();
            var key = (byte[])account.KeyPair.ClearTextPrivateKey.Clone();
            return Build(key, account.Address.ToString(), configure);
        }

        public static TreasuryHarness WithoutPlatformKey(Action<ReserveOptions>? configure = null) =>
            Build(platformKey: null, reserveAddress: string.Empty, configure);

        private static TreasuryHarness Build(
            byte[]? platformKey, string reserveAddress, Action<ReserveOptions>? configure)
        {
            var options = new ReserveOptions();
            configure?.Invoke(options);

            var ledger = new InMemoryProjectTreasuryLedgerStore();
            var custody = new FakeKeyCustodyService(platformKey);
            var factory = new Mock<IBlockchainProviderFactory>();
            factory.SetupGet(f => f.DefaultNetwork).Returns(ChainNetwork.Devnet);

            var harness = new TreasuryHarness(ledger, custody, options, reserveAddress);

            var provider = new Mock<IBlockchainProvider>();
            provider
                .Setup(p => p.GetBalanceAsync(
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string address, string? tokenId, CancellationToken token) =>
                {
                    harness.LastBalanceAddress = address;
                    return AZOAResult<string>.Success(harness.ProviderBalance);
                });
            factory
                .Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(provider.Object);

            harness.Treasury = new ProjectTreasuryService(custody, factory.Object, ledger, options);
            return harness;
        }

        public void RotatePlatformKey() =>
            Custody.PlatformKey = (byte[])new AlgorandAccount().KeyPair.ClearTextPrivateKey.Clone();

        public Task<AZOAResult<ProjectTreasuryAccount>> ResolveAsync(Guid tenantId, string projectReference) =>
            Treasury.ResolveAsync(new ProjectTreasuryReference(Chain, tenantId, projectReference));

        public Task<AZOAResult<ProjectTreasuryMovementOutcome>> MoveAsync(
            ProjectTreasuryMovementKind kind, long amount, string operationKey) =>
            Treasury.RecordMovementAsync(new ProjectTreasuryMovementRequest(
                new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha),
                AssetId,
                kind,
                new BigInteger(amount),
                operationKey));

        public Task<AZOAResult<ProjectTreasuryLedger>> CloseAsync(string reason) =>
            Treasury.CloseAsync(new ProjectTreasuryCloseRequest(
                new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha), reason, OperatorUserId));

        public async Task<ProjectTreasuryAssetCounters> CountersAsync()
        {
            var row = await Treasury.GetLedgerAsync(
                new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha));
            row.IsError.Should().BeFalse(row.Message);
            var assets = ProjectTreasuryAssetCodec.Decode(row.Result!.Assets, out var error);
            error.Should().BeEmpty();
            return assets!.TryGetValue(AssetId, out var counters)
                ? counters
                : ProjectTreasuryAssetCounters.Empty;
        }
    }

    /// <summary>
    /// Custody double that hands out a real per-chain platform key (or none), and can
    /// rotate it so the address-freeze control is exercised against a key change.
    /// </summary>
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

    /// <summary>
    /// In-memory ledger with the same compare-and-swap contract as the Surreal store: a
    /// write whose expected version has moved loses, it does not overwrite.
    /// </summary>
    private sealed class InMemoryProjectTreasuryLedgerStore : IProjectTreasuryLedgerStore
    {
        private readonly Dictionary<string, ProjectTreasuryLedger> _rows = new(StringComparer.Ordinal);

        public int Count => _rows.Count;

        /// <summary>Replaces every stored assets blob with something the codec cannot read.</summary>
        public void CorruptAssets()
        {
            foreach (var key in _rows.Keys.ToList())
                _rows[key].Assets = JsonSerializer.SerializeToElement("not-an-object");
        }

        public Task<AZOAResult<ProjectTreasuryLedger?>> GetAsync(
            string recordId, CancellationToken ct = default) =>
            Task.FromResult(new AZOAResult<ProjectTreasuryLedger?>
            {
                Result = _rows.TryGetValue(recordId, out var row) ? Clone(row) : null,
            });

        public Task<AZOAResult<ProjectTreasuryLedgerClaim>> CreateIfAbsentAsync(
            ProjectTreasuryLedger row, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(row.Id, out var existing))
                return Task.FromResult(AZOAResult<ProjectTreasuryLedgerClaim>.Success(
                    new ProjectTreasuryLedgerClaim(Clone(existing), Created: false)));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<ProjectTreasuryLedgerClaim>.Success(
                new ProjectTreasuryLedgerClaim(Clone(row), Created: true)));
        }

        public Task<AZOAResult<ProjectTreasuryLedger>> UpdateIfVersionAsync(
            ProjectTreasuryLedger row, long expectedVersion, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(row.Id, out var existing) || existing.Version != expectedVersion)
                return Task.FromResult(AZOAResult<ProjectTreasuryLedger>.Failure(
                    $"Project treasury ledger row '{row.Id}' changed underneath this update."));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<ProjectTreasuryLedger>.Success(Clone(row)));
        }

        public Task<AZOAResult<IReadOnlyList<ProjectTreasuryLedger>>> ListByTenantAsync(
            string tenantId, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<IReadOnlyList<ProjectTreasuryLedger>>.Success(
                _rows.Values
                    .Where(row => string.Equals(row.TenantId, $"user:{tenantId}", StringComparison.Ordinal))
                    .OrderByDescending(row => row.CreatedAt)
                    .Select(Clone)
                    .ToList()));

        private static ProjectTreasuryLedger Clone(ProjectTreasuryLedger row) => new()
        {
            Id = row.Id,
            Chain = row.Chain,
            Network = row.Network,
            TenantId = row.TenantId,
            ProjectReference = row.ProjectReference,
            TreasuryAddress = row.TreasuryAddress,
            DerivationVersion = row.DerivationVersion,
            Status = row.Status,
            ClosedAt = row.ClosedAt,
            ClosedByUserId = row.ClosedByUserId,
            CloseReason = row.CloseReason,
            Assets = row.Assets,
            RecentOperationKeys = row.RecentOperationKeys?.ToArray(),
            Version = row.Version,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
        };
    }
}
