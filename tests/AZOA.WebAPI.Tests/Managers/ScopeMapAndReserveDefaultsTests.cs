// SPDX-License-Identifier: UNLICENSED
// L4.5 -- three fail-opens closed: a burn-capable scope (OI-73), two configuration
// defaults that meant "no restriction" (OI-74), and a replay window that ages out (OI-75).

using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Consent;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// OI-73: a tenant-driven BURN needs its own scope. While <c>Burn</c> shared the
/// <c>nft:mint</c> entry in the operation-scope map, the <c>nft:mint</c> the
/// tenant-provisioned implied grant carries for an inbound credit would equally have
/// authorised destroying the person's asset.
/// </summary>
public class BurnScopeTests
{
    private const string BurnOperationType = "Burn";

    [Fact]
    public void A_burn_no_longer_signs_under_the_mint_scope()
    {
        // THE ATTACK: a tenant holding nft:mint drives a Burn. Before OI-73 the map said
        // yes, so the consent gate would have checked a capability the person granted for
        // being PAID against an operation that destroys what they hold.
        AzoaScopes.IsScopeValidForOperation(BurnOperationType, AzoaScopes.NftMint)
            .Should().BeFalse();
    }

    [Fact]
    public void A_burn_signs_under_its_own_scope_and_nothing_else_signs_under_that()
    {
        AzoaScopes.IsScopeValidForOperation(BurnOperationType, AzoaScopes.NftBurn)
            .Should().BeTrue();

        foreach (var otherOperation in new[]
                 {
                     "Mint", "Transfer", "Swap", "Exchange", "fungible_token_create",
                     "CredentialRevoke", AzoaScopes.IssueUnitsOperationType,
                 })
        {
            AzoaScopes.IsScopeValidForOperation(otherOperation, AzoaScopes.NftBurn)
                .Should().BeFalse($"nft:burn must not be able to sign a {otherOperation}");
        }
    }

    [Fact]
    public void No_scope_the_tenant_provisioned_implied_grant_carries_can_sign_a_burn()
    {
        // The real subject of OI-73, asserted against the real grant rather than a fixture:
        // the set a custodially onboarded person is signed up with at provisioning time
        // (Services/Consent/TenantProvisionedGrantRegistration.cs) against the map the
        // dispatch seam consults (Managers/BlockchainOperationManager.cs:593).
        foreach (var granted in TenantProvisionedGrant.ImpliedScopes)
        {
            AzoaScopes.IsScopeValidForOperation(BurnOperationType, granted)
                .Should().BeFalse(
                    $"the implied grant's '{granted}' must not reach a tenant-driven burn");
        }
    }

    [Fact]
    public void No_credential_this_node_issues_can_carry_the_burn_scope_yet()
    {
        // Fail-closed by construction: the scope exists so a Burn has somewhere legitimate
        // to sign, but nothing grants it, so a tenant-driven burn is refused at the seam
        // until a deliberate item decides who may hold it.
        AzoaScopes.IsIssuableByUser(AzoaScopes.NftBurn).Should().BeFalse();
        AzoaScopes.IssuableScopeCatalog()
            .Should().NotContain(entry => entry.Scope == AzoaScopes.NftBurn);
    }

    [Fact]
    public void The_burn_scope_can_never_arrive_through_a_no_UX_participation_grant()
    {
        // Membership here is what Managers/ConsentManager.cs:92 rejects a Participation
        // grant for, so a future path that tried to hand nft:burn out with no user gesture
        // refuses the whole grant rather than quietly widening one.
        AzoaScopes.ValueSigningScopes.Should().Contain(AzoaScopes.NftBurn);
        TenantProvisionedGrant.ImpliedScopes.Should().NotContain(AzoaScopes.NftBurn);
        TenantProvisionedGrant.CarriesNoValueSigningScope.Should().BeTrue();
    }
}

/// <summary>
/// OI-74: two configuration fail-opens. An empty <c>Reserve:Chains</c> meant "no
/// restriction", and a blank <c>Reserve:ExpectedAddresses</c> left the address-mismatch
/// guard inert on exactly the chains an operator had declared deliberately.
/// </summary>
public class ReserveConfigurationFailClosedTests
{
    private const string ShippedChain = "Algorand";
    private const string OtherChain = "Solana";

    [Fact]
    public void An_undeclared_chain_list_permits_only_the_chain_this_node_ships()
    {
        var options = new ReserveOptions();

        options.Chains.Should().BeEmpty("this is the shape a node with no Reserve section binds to");
        options.PermitsChain(ShippedChain).Should().BeTrue();
        options.PermitsChain(OtherChain).Should().BeFalse(
            "an unconfigured node must not silently start holding value on every chain");
        options.PermitsChain(null).Should().BeFalse();
        options.PermitsChain("   ").Should().BeFalse();
    }

    [Fact]
    public void A_declared_chain_list_is_authoritative_and_the_fallback_never_widens_it()
    {
        var options = new ReserveOptions { Chains = { OtherChain } };

        options.PermitsChain(OtherChain).Should().BeTrue();
        options.PermitsChain(ShippedChain).Should().BeFalse(
            "the shipped-chain fallback exists for an EMPTY list, not as an addition to a declared one");
    }

    [Fact]
    public async Task Signing_as_the_reserve_on_an_undeclared_chain_never_reaches_custody()
    {
        var custody = new Mock<IKeyCustodyService>();
        var reserve = Build(new ReserveOptions(), custody);

        var signed = await reserve.SignAsReserveAsync(
            OtherChain, SigningContext.PlatformFor(OtherChain), _ => Task.FromResult("signature"));

        signed.IsError.Should().BeTrue();
        signed.Message.Should().Contain("Reserve:Chains");
        VerifyNoKeyWasTouched(custody);
    }

    [Fact]
    public async Task A_declared_chain_with_no_declared_reserve_address_is_refused_before_any_key()
    {
        // The OI-74 fail-open: appsettings names the chain, ExpectedAddresses is blank, and
        // the mismatch guard therefore compared nothing at all. It is a refusal now.
        var custody = new Mock<IKeyCustodyService>();
        var reserve = Build(new ReserveOptions { Chains = { ShippedChain } }, custody);

        var signed = await reserve.SignAsReserveAsync(
            ShippedChain, SigningContext.PlatformFor(ShippedChain), _ => Task.FromResult("signature"));

        signed.IsError.Should().BeTrue();
        signed.Message.Should().Contain("ExpectedAddresses");
        VerifyNoKeyWasTouched(custody);
    }

    [Fact]
    public async Task A_whitespace_only_declared_address_does_not_count_as_a_declaration()
    {
        // The shape an env var set to "" produces. It resolves to the narrowest reading.
        var custody = new Mock<IKeyCustodyService>();
        var options = new ReserveOptions
        {
            Chains = { ShippedChain },
            ExpectedAddresses = { [ShippedChain] = "   " },
        };

        var signed = await Build(options, custody).SignAsReserveAsync(
            ShippedChain, SigningContext.PlatformFor(ShippedChain), _ => Task.FromResult("signature"));

        signed.IsError.Should().BeTrue();
        signed.Message.Should().Contain("ExpectedAddresses");
        VerifyNoKeyWasTouched(custody);
    }

    [Fact]
    public async Task Declaring_the_address_lets_the_signature_through()
    {
        var custody = new Mock<IKeyCustodyService>();
        var options = new ReserveOptions
        {
            Chains = { ShippedChain },
            ExpectedAddresses = { [ShippedChain] = "RESERVEADDRESSTHEOPERATORDECLARED" },
        };

        var signed = await Build(options, custody).SignAsReserveAsync(
            ShippedChain, SigningContext.PlatformFor(ShippedChain), _ => Task.FromResult("signature"));

        signed.IsError.Should().BeFalse(signed.Message);
        signed.Result.Should().Be("signature");
    }

    [Fact]
    public async Task A_node_that_declared_no_chain_is_not_held_to_the_address_declaration()
    {
        // The undeclared node runs on the shipped-chain fallback and makes no claim about
        // where it holds value, so it is not held to completing one. That path was already
        // narrowed from every chain to one above.
        var custody = new Mock<IKeyCustodyService>();

        var signed = await Build(new ReserveOptions(), custody).SignAsReserveAsync(
            ShippedChain, SigningContext.PlatformFor(ShippedChain), _ => Task.FromResult("signature"));

        signed.IsError.Should().BeFalse(signed.Message);
    }

    [Fact]
    public async Task The_operator_can_opt_out_of_the_address_assertion_only_deliberately()
    {
        var custody = new Mock<IKeyCustodyService>();
        var options = new ReserveOptions
        {
            Chains = { ShippedChain },
            AllowUndeclaredReserveAddress = true,
        };

        var signed = await Build(options, custody).SignAsReserveAsync(
            ShippedChain, SigningContext.PlatformFor(ShippedChain), _ => Task.FromResult("signature"));

        signed.IsError.Should().BeFalse(signed.Message);
    }

    private static IPlatformReserve Build(ReserveOptions options, Mock<IKeyCustodyService> custody)
    {
        custody
            .Setup(service => service.WithPlatformSigningKeyAsync(
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<SigningContext>(),
                It.IsAny<Func<byte[], Task<string>>>()))
            .ReturnsAsync(AZOAResult<string>.Success("signature"));

        var providers = new Mock<IBlockchainProviderFactory>();
        providers.SetupGet(factory => factory.DefaultNetwork).Returns(ChainNetwork.Devnet);

        return new PlatformReserveService(
            custody.Object, providers.Object, new FakeReserveLedgerStore(), options);
    }

    private static void VerifyNoKeyWasTouched(Mock<IKeyCustodyService> custody) =>
        custody.Verify(
            service => service.WithPlatformSigningKeyAsync(
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<SigningContext>(),
                It.IsAny<Func<byte[], Task<string>>>()),
            Times.Never,
            "a configuration refusal must never cost a key decrypt");
}

/// <summary>
/// OI-75: the draw replay window is a bounded FIFO per asset row, so the guarantee it
/// gives is narrower than "a retried draw cannot double-count a partition". These pin the
/// real boundary and prove the anchor the live hold caller relies on instead.
/// </summary>
public class ReserveDrawReplayWindowTests
{
    private const string Chain = "Algorand";
    private const string AssetId = "1001";
    private const string Partition = "contributor";

    [Fact]
    public async Task A_retry_inside_the_window_replays_and_moves_nothing()
    {
        var (reserve, ledger) = Build();

        var first = await Draw(reserve, "op-1");
        var replay = await Draw(reserve, "op-1");

        first.IsError.Should().BeFalse(first.Message);
        first.Result!.Replayed.Should().BeFalse();
        replay.IsError.Should().BeFalse(replay.Message);
        replay.Result!.Replayed.Should().BeTrue();
        IssuedOn(ledger).Should().Be(BigInteger.One, "the counter itself must not have moved");
    }

    [Fact]
    public async Task A_retry_whose_key_aged_out_of_the_window_is_applied_a_second_time()
    {
        // This is the honest statement of the guarantee, written as a test so the narrower
        // claim in PlatformReserveService.DrawReplayWindowSize cannot drift back to the
        // unconditional one. It is not a defect being accepted quietly: it is why every
        // draw seam has to bring a durable anchor (see the hold anchor below).
        var (reserve, ledger) = Build();

        await Draw(reserve, "op-aged-out");
        for (var index = 0; index < PlatformReserveService.DrawReplayWindowSize; index++)
            await Draw(reserve, $"op-{index}");

        var retriedTooLate = await Draw(reserve, "op-aged-out");

        retriedTooLate.IsError.Should().BeFalse(retriedTooLate.Message);
        retriedTooLate.Result!.Replayed.Should().BeFalse(
            "the key is no longer in the window, so the reserve has no memory of it");
        IssuedOn(ledger).Should().Be(
            new BigInteger(PlatformReserveService.DrawReplayWindowSize + 2),
            "the aged-out retry was applied on top of the original draw");
    }

    private static Task<AZOAResult<ReserveDrawOutcome>> Draw(IPlatformReserve reserve, string operationKey) =>
        reserve.DrawAsync(new ReserveDrawRequest(
            Chain, AssetId, Partition, ReserveDrawKind.Issue, BigInteger.One,
            operationKey, ReserveDrawAuthority.NodeOperator));

    private static BigInteger IssuedOn(FakeReserveLedgerStore ledger)
    {
        var partitions = ReservePartitionCodec.Decode(ledger.Single().Partitions, out var error);
        partitions.Should().NotBeNull(error);
        return partitions![Partition].Issued;
    }

    private static (IPlatformReserve Reserve, FakeReserveLedgerStore Ledger) Build()
    {
        var providers = new Mock<IBlockchainProviderFactory>();
        providers.SetupGet(factory => factory.DefaultNetwork).Returns(ChainNetwork.Devnet);

        var ledger = new FakeReserveLedgerStore();
        ledger.Seed(new PlatformReserveLedger
        {
            Id = PlatformReserveLedger.RecordIdFor(Chain, ChainNetwork.Devnet.ToString(), AssetId),
            AssetId = AssetId,
            Chain = Chain,
            Network = ChainNetwork.Devnet.ToString(),
            ReserveAddress = "RESERVEADDRESS",
            TenantId = null,
            UnitName = "UNIT",
            Decimals = 2,
            TotalSupply = "1000000",
            Partitions = ReservePartitionCodec.Encode(new Dictionary<string, ReservePartitionCounters>
            {
                [Partition] = ReservePartitionCounters.Declaring(new BigInteger(1000000)),
            }),
            RecentDrawKeys = Array.Empty<string>(),
            Version = 0,
        });

        var reserve = new PlatformReserveService(
            Mock.Of<IKeyCustodyService>(), providers.Object, ledger, new ReserveOptions());

        return (reserve, ledger);
    }
}

/// <summary>
/// OI-75, the other half: the hold's own durable anchor. The reserve window cannot be the
/// anchor, so the hold row's state is -- and a hold parked in <c>Releasing</c> by a failed
/// payout must never draw its release again when the sweep retries it.
/// </summary>
public class HoldDrawAnchorTests
{
    [Fact]
    public void A_hold_already_releasing_never_draws_its_release_again()
    {
        // Before OI-75 this state re-entered the draw on every retry, and only the reserve's
        // 128-entry window stood between a retried payout and a partition counted twice.
        PlatformHoldService.DrawsFrom(HoldDrawStage.Release, HoldState.Releasing)
            .Should().BeFalse();
        PlatformHoldService.DrawsFrom(HoldDrawStage.Release, HoldState.Held)
            .Should().BeTrue("Held is the one state whose release draw is still owed");
    }

    [Fact]
    public void Each_stage_draws_from_exactly_one_state()
    {
        var states = Enum.GetValues<HoldState>();

        foreach (var stage in Enum.GetValues<HoldDrawStage>())
        {
            states.Count(state => PlatformHoldService.DrawsFrom(stage, state))
                .Should().Be(1, $"{stage} must have exactly one state its draw is owed from");
        }
    }

    [Theory]
    [InlineData(HoldState.Released)]
    [InlineData(HoldState.Refunded)]
    [InlineData(HoldState.ReleasedIntoHold)]
    public void A_terminal_hold_draws_nothing_under_any_stage(HoldState terminal)
    {
        foreach (var stage in Enum.GetValues<HoldDrawStage>())
        {
            PlatformHoldService.DrawsFrom(stage, terminal)
                .Should().BeFalse($"{terminal} is terminal, so no {stage} draw is owed");
        }
    }

    [Fact]
    public void An_opening_draw_is_owed_only_before_the_row_moved_to_held()
    {
        PlatformHoldService.DrawsFrom(HoldDrawStage.Open, HoldState.Pending).Should().BeTrue();
        PlatformHoldService.DrawsFrom(HoldDrawStage.Open, HoldState.Held).Should().BeFalse();
    }
}

/// <summary>In-memory reserve ledger with the same compare-and-swap contract as Surreal's.</summary>
internal sealed class FakeReserveLedgerStore : IPlatformReserveLedgerStore
{
    private readonly Dictionary<string, PlatformReserveLedger> _rows = new(StringComparer.Ordinal);

    public void Seed(PlatformReserveLedger row) => _rows[row.Id] = Copy(row);

    public PlatformReserveLedger Single() => _rows.Values.Single();

    public Task<AZOAResult<PlatformReserveLedger?>> GetAsync(string recordId, CancellationToken ct = default) =>
        Task.FromResult(AZOAResult<PlatformReserveLedger?>.Success(
            _rows.TryGetValue(recordId, out var row) ? Copy(row) : (PlatformReserveLedger?)null));

    public Task<AZOAResult<PlatformReserveLedgerClaim>> CreateIfAbsentAsync(
        PlatformReserveLedger row, CancellationToken ct = default)
    {
        if (_rows.TryGetValue(row.Id, out var existing))
            return Task.FromResult(AZOAResult<PlatformReserveLedgerClaim>.Success(
                new PlatformReserveLedgerClaim(Copy(existing), false)));

        _rows[row.Id] = Copy(row);
        return Task.FromResult(AZOAResult<PlatformReserveLedgerClaim>.Success(
            new PlatformReserveLedgerClaim(Copy(row), true)));
    }

    public Task<AZOAResult<PlatformReserveLedger>> UpdateIfVersionAsync(
        PlatformReserveLedger row, long expectedVersion, CancellationToken ct = default)
    {
        if (!_rows.TryGetValue(row.Id, out var current) || current.Version != expectedVersion)
            return Task.FromResult(AZOAResult<PlatformReserveLedger>.Failure(
                "The reserve ledger row moved under this write."));

        _rows[row.Id] = Copy(row);
        return Task.FromResult(AZOAResult<PlatformReserveLedger>.Success(Copy(row)));
    }

    /// <summary>A read hands back a detached row, exactly as a round trip through the database would.</summary>
    private static PlatformReserveLedger Copy(PlatformReserveLedger row) => new()
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
        Partitions = row.Partitions?.Clone(),
        RecentDrawKeys = row.RecentDrawKeys?.ToList(),
        Version = row.Version,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };
}
