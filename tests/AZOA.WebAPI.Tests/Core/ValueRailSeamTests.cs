using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AZOA.WebAPI.Tests.Core;

/// <summary>
/// The value-rail seam (decision D9): a rail that is NOT a blockchain must be
/// registrable and resolvable, its settlement must be describable as a reversal window
/// rather than a finality flag, and every existing chain call site must keep working.
/// </summary>
/// <remarks>
/// Before this seam a fiat rail was <b>type-impossible</b> to register: the factory took
/// <c>Func&lt;IBlockchainProvider&gt;</c> and returned <see cref="IBlockchainProvider"/>,
/// and its network concept was Devnet/Testnet/Mainnet — none of which a payment
/// processor has. These tests pin that both blockers are gone without loosening the
/// chain path.
/// </remarks>
public sealed class ValueRailSeamTests
{
    private const string FiatRailId = "Fiat.USD";

    // ═══════════════════════════════════════════════════════════════════════
    // SettlementProfile — a reversal WINDOW, never a finality claim
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Chain_settlement_is_irreversible_so_escrow_may_release_at_settlement_time()
    {
        var settledAt = DateTimeOffset.UtcNow;
        var profile = SettlementProfile.IrreversibleOnce("the transaction is confirmed on chain");

        profile.Reversibility.Should().Be(SettlementReversibility.Irreversible);
        profile.ReversalWindow.Should().Be(TimeSpan.Zero);
        profile.TryGetReleaseTimeUtc(settledAt, out var releaseAt).Should().BeTrue();
        releaseAt.Should().Be(settledAt, "a confirmed chain transaction cannot be clawed back");
    }

    [Fact]
    public void A_chargeback_tail_delays_release_by_the_whole_window_not_by_a_flag()
    {
        // The case a boolean IsFinal gets wrong and costs money: the money IS in the
        // payee's account the moment the card settles, and is still not theirs.
        var settledAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var profile = SettlementProfile.ReversibleFor(TimeSpan.FromDays(120), "the card capture settles");

        profile.Reversibility.Should().Be(SettlementReversibility.ReversibleWithinWindow);
        profile.TryGetReleaseTimeUtc(settledAt, out var releaseAt).Should().BeTrue();
        releaseAt.Should().Be(settledAt.AddDays(120));
    }

    [Fact]
    public void An_unbounded_reversal_rail_has_no_release_time_at_all()
    {
        var profile = SettlementProfile.ReversibleWithoutBound("the transfer posts");

        profile.Reversibility.Should().Be(SettlementReversibility.UnboundedReversal);
        profile.TryGetReleaseTimeUtc(DateTimeOffset.UtcNow, out _)
            .Should().BeFalse("'there is no such instant' is an answer a boolean cannot give");
    }

    [Fact]
    public void A_reversible_profile_must_declare_a_positive_window()
    {
        var construct = () => SettlementProfile.ReversibleFor(TimeSpan.Zero, "nothing");

        construct.Should().Throw<ArgumentOutOfRangeException>(
            "a zero-window 'reversible' rail is an irreversible rail wearing a disguise");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // RailEnvironment — the network concept is no longer chain-shaped
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(ChainNetwork.Devnet, RailEnvironmentKind.Test)]
    [InlineData(ChainNetwork.Testnet, RailEnvironmentKind.Test)]
    [InlineData(ChainNetwork.Mainnet, RailEnvironmentKind.Live)]
    public void A_chain_network_maps_to_a_rail_environment_and_back(
        ChainNetwork network, RailEnvironmentKind expectedKind)
    {
        var environment = RailEnvironment.ForChain(network);

        environment.Kind.Should().Be(expectedKind);
        environment.TryToChainNetwork(out var roundTripped).Should().BeTrue();
        roundTripped.Should().Be(network);
    }

    [Fact]
    public void A_fiat_environment_never_silently_becomes_a_chain_network()
    {
        RailEnvironment.Live.TryToChainNetwork(out _)
            .Should().BeFalse("a payment rail's 'live' is not Mainnet, and guessing would bind the wrong config");
        RailEnvironment.Test.TryToChainNetwork(out _).Should().BeFalse();
        RailEnvironment.Live.Kind.Should().Be(RailEnvironmentKind.Live);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // The factory can hold and resolve a rail that is not a chain
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Factory_registers_and_resolves_a_non_chain_value_rail()
    {
        var factory = NewFactoryWithFiatRail();

        var rail = factory.GetRailProvider(FiatRailId, RailEnvironment.Live);

        rail.Should().BeOfType<FakeFiatRailProvider>();
        rail.Should().NotBeAssignableTo<IBlockchainProvider>(
            "the whole point is a rail that is not a chain provider");
        rail.RailId.Should().Be(FiatRailId);
        rail.ActiveEnvironment.Kind.Should().Be(RailEnvironmentKind.Live);
        rail.SettlementProfile.Reversibility.Should().Be(SettlementReversibility.ReversibleWithinWindow);
        rail.SettlementProfile.ReversalWindow.Should().Be(TimeSpan.FromDays(120));
    }

    [Fact]
    public void Factory_caches_one_instance_per_rail_environment()
    {
        var factory = NewFactoryWithFiatRail();

        var live = factory.GetRailProvider(FiatRailId, RailEnvironment.Live);
        var liveAgain = factory.GetRailProvider(FiatRailId, RailEnvironment.Live);
        var test = factory.GetRailProvider(FiatRailId, RailEnvironment.Test);

        liveAgain.Should().BeSameAs(live);
        test.Should().NotBeSameAs(live, "test and live must never share a bound instance");
        test.ActiveEnvironment.Kind.Should().Be(RailEnvironmentKind.Test);
    }

    [Fact]
    public void A_non_chain_rail_is_not_resolvable_through_the_chain_typed_api()
    {
        var factory = NewFactoryWithFiatRail();

        var act = () => factory.GetProvider(FiatRailId, ChainNetwork.Mainnet);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*is not a blockchain*")
            .WithMessage("*IValueRailProvider*");
    }

    [Fact]
    public void A_rail_environment_the_registration_disables_is_refused_not_invented()
    {
        var factory = new BlockchainProviderFactory(
            new ValueRailProviderRegistration[]
            {
                new(FiatRailId, () => new FakeFiatRailProvider(FiatRailId),
                    // Live only: the sandbox is not provisioned.
                    environment => environment.Kind == RailEnvironmentKind.Live
                        ? new BlockchainNetworkConfig { IsEnabled = true }
                        : null),
            },
            EmptyBlockchainConfig());

        var act = () => factory.GetRailProvider(FiatRailId, RailEnvironment.Test);

        act.Should().Throw<InvalidOperationException>().WithMessage("*is disabled for value rail*");
        factory.TryGetRailProvider(FiatRailId, RailEnvironment.Test, out _).Should().BeFalse();
        factory.TryGetRailProvider(FiatRailId, RailEnvironment.Live, out var live).Should().BeTrue();
        live!.RailId.Should().Be(FiatRailId);
    }

    [Fact]
    public void Registering_a_chain_and_a_non_chain_rail_side_by_side_keeps_both_paths_working()
    {
        var factory = new BlockchainProviderFactory(
            new ValueRailProviderRegistration[]
            {
                new BlockchainProviderRegistration(
                    "Simulated",
                    () => new AZOA.WebAPI.Providers.Blockchain.Simulated.SimulatedBlockchainProvider(
                        SimulatedChainConfig(),
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<
                            AZOA.WebAPI.Providers.Blockchain.Simulated.SimulatedBlockchainProvider>.Instance)),
                new(FiatRailId, () => new FakeFiatRailProvider(FiatRailId),
                    _ => new BlockchainNetworkConfig { IsEnabled = true }),
            },
            SimulatedChainConfig());

        factory.GetProvider("Simulated", ChainNetwork.Devnet).ChainType.Should().Be("Simulated");
        factory.GetRailProvider(FiatRailId, RailEnvironment.Live).RailId.Should().Be(FiatRailId);
        factory.RegisteredRailIds.Should().BeEquivalentTo(new[] { "Simulated", FiatRailId });
    }

    [Fact]
    public void Two_registrations_for_one_rail_are_a_wiring_bug_not_a_silent_winner()
    {
        var act = () => new BlockchainProviderFactory(
            new ValueRailProviderRegistration[]
            {
                new(FiatRailId, () => new FakeFiatRailProvider(FiatRailId),
                    _ => new BlockchainNetworkConfig { IsEnabled = true }),
                new(FiatRailId, () => new FakeFiatRailProvider(FiatRailId),
                    _ => new BlockchainNetworkConfig { IsEnabled = true }),
            },
            EmptyBlockchainConfig());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate value rail provider registration*");
    }

    [Fact]
    public void An_unregistered_rail_still_surfaces_the_familiar_not_found_exception()
    {
        var factory = NewFactoryWithFiatRail();

        var act = () => factory.GetRailProvider("Fiat.EUR", RailEnvironment.Live);

        // Derives from ValueRailProviderNotFoundException, so every existing chain
        // call site that catches BlockchainProviderNotFoundException keeps working.
        act.Should().Throw<ValueRailProviderNotFoundException>();
        factory.TryGetRailProvider("Fiat.EUR", RailEnvironment.Live, out _).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // The capability-honesty boot guard still reads config directly
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Guard_catches_an_enabled_but_unregistered_chain_in_the_real_shipped_appsettings()
    {
        // The real file, not a fixture: this is what actually boots. Algorand IS enabled
        // there, so omitting it from the registered list must be caught.
        var config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: false).Build();

        var act = () => BlockchainProviderFactory.GuardAgainstUnregisteredEnabledChains(
            config, new[] { "Solana", "Simulated" });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no registered provider*Algorand*");
    }

    [Fact]
    public void Guard_passes_the_real_shipped_appsettings_against_the_chains_Program_registers()
    {
        // Keep this list identical to `registeredBlockchainChainTypes` in Program.cs.
        var config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: false).Build();

        var act = () => BlockchainProviderFactory.GuardAgainstUnregisteredEnabledChains(
            config, new[] { "Algorand", "Solana", "Ethereum", "Simulated" });

        act.Should().NotThrow();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static BlockchainProviderFactory NewFactoryWithFiatRail() =>
        new(
            new ValueRailProviderRegistration[]
            {
                new(FiatRailId, () => new FakeFiatRailProvider(FiatRailId),
                    _ => new BlockchainNetworkConfig { IsEnabled = true }),
            },
            EmptyBlockchainConfig());

    private static IConfiguration EmptyBlockchainConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static IConfiguration SimulatedChainConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultChain"] = "Simulated",
                ["Blockchain:Chains:0:ChainType"] = "Simulated",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            })
            .Build();

    /// <summary>
    /// A value rail that is deliberately NOT an <see cref="IBlockchainProvider"/> — the
    /// shape decision D9 exists to make registrable. It is a stand-in for the fiat rail,
    /// not the fiat rail: it settles nothing and is never wired into the app.
    /// </summary>
    private sealed class FakeFiatRailProvider : IValueRailProvider
    {
        private RailEnvironment _environment;

        public FakeFiatRailProvider(string railId) => RailId = railId;

        public string RailId { get; }

        public RailEnvironment ActiveEnvironment => _environment;

        public SettlementProfile SettlementProfile { get; } =
            SettlementProfile.ReversibleFor(TimeSpan.FromDays(120), "the card capture settles");

        public void InitializeRail(ValueRailBinding binding) => _environment = binding.Environment;

        public Task<AZOAResult<string>> GetBalanceAsync(
            string address, string? tokenId = null, CancellationToken ct = default) =>
            Task.FromResult(new AZOAResult<string> { Result = "0" });

        public Task<AZOAResult<bool>> ValidateAddressAsync(string address, CancellationToken ct = default) =>
            Task.FromResult(new AZOAResult<bool> { Result = !string.IsNullOrWhiteSpace(address) });

        public Task<AZOAResult<string>> TransferAsync(
            string tokenId, string fromAddress, string toAddress, RailAmount amount,
            SigningContext? signingContext = null, CancellationToken ct = default)
        {
            amount.Dimension.Should().Be(ValueDimension.Usd, "a fiat rail settles fiat, not chain base units");
            return Task.FromResult(new AZOAResult<string> { Result = "payout_1" });
        }

        public Task<AZOAResult<Dictionary<string, object>>> GetTransactionStatusAsync(
            string txHash, CancellationToken ct = default) =>
            Task.FromResult(new AZOAResult<Dictionary<string, object>> { Result = new() });

        public Task<AZOAResult<ChainConfirmation>> GetTransactionConfirmationAsync(
            string txHash, CancellationToken ct = default) =>
            Task.FromResult(new AZOAResult<ChainConfirmation> { Result = ChainConfirmation.Unknown });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // RailAmount dimension behaviour used by the rail above
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_non_chain_rail_moves_value_through_the_shared_transfer_surface()
    {
        var factory = NewFactoryWithFiatRail();
        var rail = factory.GetRailProvider(FiatRailId, RailEnvironment.Live);

        var result = await rail.TransferAsync(
            tokenId: null!, "acct_payer", "acct_payee", RailAmount.OfUsdMinorUnits(12_345));

        result.IsError.Should().BeFalse();
        result.Result.Should().Be("payout_1");
    }

    [Fact]
    public void The_currency_dimension_is_data_so_a_second_currency_is_a_value_change()
    {
        ValueDimension.Usd.Code.Should().Be("USD");
        ValueDimension.Fiat("eur").Should().Be(ValueDimension.Fiat("EUR"),
            "case must not split one currency into two dimensions");
        ValueDimension.Fiat("EUR").Should().NotBe(ValueDimension.Usd);
        ValueDimension.Usd.IsFiat.Should().BeTrue();
        ValueDimension.NativeChainAsset.IsFiat.Should().BeFalse();
        ValueDimension.NativeChainAsset.Should().Be(default(ValueDimension),
            "the common chain case must not require a second spelling of the asset");
    }

    [Fact]
    public void An_amount_survives_a_canonical_string_round_trip_above_the_old_ceiling()
    {
        var twentyEtherInWei = BigInteger.Parse("20000000000000000000");
        var amount = RailAmount.OfChainAsset(twentyEtherInWei);

        RailAmount.TryParse(amount.ToBaseUnitString(), ValueDimension.NativeChainAsset, out var parsed)
            .Should().BeTrue();
        parsed.Should().Be(amount);
        parsed.BaseUnits.Should().Be(twentyEtherInWei);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.5")]
    [InlineData("1,000")]
    [InlineData("0x10")]
    public void Parsing_refuses_anything_that_is_not_a_canonical_base_unit_count(string text)
    {
        RailAmount.TryParse(text, ValueDimension.NativeChainAsset, out _).Should().BeFalse();
    }
}
