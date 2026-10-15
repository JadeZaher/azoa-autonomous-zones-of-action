using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using FluentAssertions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Actions.Workflow;

/// <summary>
/// The rail-ROUTING half of the value-rail capability gate (decision D9): which rail the
/// question is asked about, and whether the answer is scoped to it.
/// </summary>
/// <remarks>
/// The old gate asked "does this actor have ANY wallet bound", which was wrong in both
/// directions the instant a second rail existed:
/// <list type="bullet">
/// <item>an Algorand-wallet holder PASSED a fiat payout they cannot receive;</item>
/// <item>a fiat-only user — the person who should never see a blockchain — FAILED a
/// pure fiat payout.</item>
/// </list>
/// Both are pinned here, along with the fail-closed behaviour that must survive.
/// </remarks>
public sealed class ValueRailGateRoutingTests
{
    private const string FiatRail = "Fiat.USD";
    private const string DefaultRail = "Algorand";

    private static readonly Guid ChainWalletHolder = Guid.NewGuid();
    private static readonly Guid FiatOnlyActor = Guid.NewGuid();
    private static readonly Guid ActorWithNothing = Guid.NewGuid();

    // ═══════════════════════════════════════════════════════════════════════
    // The two directions the rail-blind gate got wrong
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_fiat_only_actor_passes_a_fiat_rail_operation()
    {
        // The persona the rail-blind gate locked out: no wallet anywhere, a registered
        // payout destination on the fiat rail, and a fiat payout owed to them.
        var gate = NewGate(
            wallets: WalletsByUser((FiatOnlyActor, Array.Empty<string>())),
            fiatDestinationHolders: new[] { FiatOnlyActor });

        var allowed = await gate.HasUsableDestinationAsync(FiatOnlyActor, FiatRail);

        allowed.Should().BeTrue("a fiat-only user must be able to receive a fiat payout without a wallet");
    }

    [Fact]
    public async Task A_chain_wallet_holder_is_refused_a_fiat_rail_operation()
    {
        // The direction that let value leave: an Algorand wallet is not somewhere a card
        // network can pay, so holding one is not capability on the fiat rail.
        var gate = NewGate(
            wallets: WalletsByUser((ChainWalletHolder, new[] { "Algorand" })),
            fiatDestinationHolders: new[] { FiatOnlyActor });

        var allowed = await gate.HasUsableDestinationAsync(ChainWalletHolder, FiatRail);

        allowed.Should().BeFalse("an Algorand wallet is not a destination a fiat payout can land in");
    }

    [Fact]
    public async Task A_chain_operation_with_no_wallet_at_all_is_still_refused()
    {
        var gate = NewGate(
            wallets: WalletsByUser((ActorWithNothing, Array.Empty<string>())),
            fiatDestinationHolders: Array.Empty<Guid>());

        (await gate.HasUsableDestinationAsync(ActorWithNothing, DefaultRail)).Should().BeFalse();
        (await gate.HasUsableDestinationAsync(ActorWithNothing, railId: null)).Should().BeFalse();
    }

    [Fact]
    public async Task A_fiat_only_actor_is_refused_a_chain_operation()
    {
        var gate = NewGate(
            wallets: WalletsByUser((FiatOnlyActor, Array.Empty<string>())),
            fiatDestinationHolders: new[] { FiatOnlyActor });

        (await gate.HasUsableDestinationAsync(FiatOnlyActor, DefaultRail)).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Chain-to-chain scoping — the same bug, one rail over
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_wallet_on_one_chain_is_not_capability_on_another()
    {
        var gate = NewGate(
            wallets: WalletsByUser((ChainWalletHolder, new[] { "Algorand" })),
            fiatDestinationHolders: Array.Empty<Guid>());

        (await gate.HasUsableDestinationAsync(ChainWalletHolder, "Algorand")).Should().BeTrue();
        (await gate.HasUsableDestinationAsync(ChainWalletHolder, "Solana")).Should().BeFalse();
        (await gate.HasUsableDestinationAsync(ChainWalletHolder, "ethereum")).Should().BeFalse();
    }

    [Fact]
    public async Task Rail_matching_is_case_insensitive()
    {
        var gate = NewGate(
            wallets: WalletsByUser((ChainWalletHolder, new[] { "Algorand" })),
            fiatDestinationHolders: Array.Empty<Guid>());

        (await gate.HasUsableDestinationAsync(ChainWalletHolder, "ALGORAND")).Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Fail-closed
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_wallet_lookup_error_is_no_capability_never_an_optimistic_yes()
    {
        var wallets = new Mock<IWalletManager>();
        wallets.Setup(m => m.QueryAsync(It.IsAny<WalletQueryRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { IsError = true, Message = "database unavailable" });

        var gate = new ValueRailCapabilityGate(wallets.Object, DefaultRail);

        (await gate.HasUsableDestinationAsync(ChainWalletHolder, DefaultRail)).Should().BeFalse();
    }

    [Fact]
    public async Task A_directory_that_throws_is_no_capability_never_an_exception_at_the_seam()
    {
        var gate = new ValueRailCapabilityGate(
            WalletsByUser((FiatOnlyActor, Array.Empty<string>())),
            DefaultRail,
            new IRailDestinationDirectory[] { new ThrowingDirectory(FiatRail) });

        (await gate.HasUsableDestinationAsync(FiatOnlyActor, FiatRail)).Should().BeFalse(
            "a directory that faults is indistinguishable from one that does not know");
    }

    [Fact]
    public async Task An_unknown_rail_with_no_directory_and_no_wallet_is_refused()
    {
        var gate = NewGate(
            wallets: WalletsByUser((ChainWalletHolder, new[] { "Algorand" })),
            fiatDestinationHolders: Array.Empty<Guid>());

        (await gate.HasUsableDestinationAsync(ChainWalletHolder, "Fiat.EUR")).Should().BeFalse();
    }

    [Fact]
    public void Two_directories_for_one_rail_are_a_wiring_bug()
    {
        var act = () => new ValueRailCapabilityGate(
            WalletsByUser(),
            DefaultRail,
            new IRailDestinationDirectory[]
            {
                new FakeFiatDestinationDirectory(FiatRail, Array.Empty<Guid>()),
                new FakeFiatDestinationDirectory(FiatRail, Array.Empty<Guid>()),
            });

        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate rail destination directory*");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Which rail the question is asked about
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void A_node_that_names_no_rail_settles_on_the_platform_default()
    {
        var gate = new ValueRailCapabilityGate(WalletsByUser(), "Solana");

        gate.ResolveRailId(null).Should().Be("Solana");
        gate.ResolveRailId("  ").Should().Be("Solana");
        gate.ResolveRailId("Algorand").Should().Be("Algorand");
    }

    [Fact]
    public void A_blank_configured_default_falls_back_to_the_same_rail_the_config_class_does()
    {
        // The gate and BlockchainConfig must not invent two different defaults that drift.
        new ValueRailCapabilityGate(WalletsByUser(), defaultRailId: null)
            .ResolveRailId(null).Should().Be(BlockchainConfig.DefaultChainFallback);
    }

    [Fact]
    public void A_bridge_node_declares_its_source_chain_as_the_settling_rail()
    {
        // The outbound leg is signed on the SOURCE chain — that is where the actor's
        // value must be, so that is the rail the gate has to ask about.
        var handler = new BridgeNodeHandler(Mock.Of<ICrossChainBridgeService>());
        var node = new ActionNode
        {
            NodeType = ActionNodeType.Bridge,
            Config = """{"SourceChain":"Solana","TargetChain":"Algorand","TokenId":"m","RecipientAddress":"r","Amount":"1"}""",
        };

        handler.ResolveSettlementRailId(node).Should().Be("Solana");
    }

    [Fact]
    public void A_fungible_token_create_node_declares_its_configured_chain()
    {
        var handler = new FungibleTokenCreateNodeHandler(
            Mock.Of<IFungibleTokenManager>(), Mock.Of<IHolonManager>());
        var node = new ActionNode
        {
            NodeType = ActionNodeType.FungibleTokenCreate,
            Config = """{"Name":"t","UnitName":"T","ChainType":"Solana","Total":100,"Decimals":0}""",
        };

        handler.ResolveSettlementRailId(node).Should().Be("Solana");
    }

    [Fact]
    public void A_node_with_unreadable_config_declares_no_rail_and_takes_the_default()
    {
        var handler = new BridgeNodeHandler(Mock.Of<ICrossChainBridgeService>());
        var node = new ActionNode { NodeType = ActionNodeType.Bridge, Config = "not json" };

        handler.ResolveSettlementRailId(node).Should().BeNull(
            "an unreadable config is the handler's problem to report, not the gate's to guess");
    }

    [Fact]
    public void A_value_free_node_declares_no_rail_at_all()
    {
        // Through the interface: the default member is what the dispatch seams call.
        IActionNodeHandler handler = new TransferNodeHandler(Mock.Of<INftManager>());

        handler.ResolveSettlementRailId(new ActionNode { NodeType = ActionNodeType.Transfer })
            .Should().BeNull("a node whose config names no chain settles on the platform default");
    }

    [Fact]
    public void The_refusal_message_names_the_rail_the_actor_lacks()
    {
        ValueRailCapabilityGate.NoDestinationMessage(FiatRail)
            .Should().Contain(FiatRail)
            .And.NotContain("wallet",
                "'no wallet bound' is not actionable for a user who was never meant to have one");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static ValueRailCapabilityGate NewGate(
        IWalletManager wallets, IReadOnlyCollection<Guid> fiatDestinationHolders) =>
        new(wallets, DefaultRail,
            new IRailDestinationDirectory[] { new FakeFiatDestinationDirectory(FiatRail, fiatDestinationHolders) });

    /// <summary>
    /// A wallet manager that honours the <see cref="WalletQueryRequest.ChainType"/> filter,
    /// like a real store does — so a test cannot pass by accident on an unscoped mock.
    /// </summary>
    private static IWalletManager WalletsByUser(params (Guid UserId, string[] ChainTypes)[] owners)
    {
        var byUser = owners.ToDictionary(o => o.UserId, o => o.ChainTypes);
        var mock = new Mock<IWalletManager>();
        mock.Setup(m => m.QueryAsync(It.IsAny<WalletQueryRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((WalletQueryRequest query, Guid userId, AZOARequest? _) =>
            {
                var chains = byUser.TryGetValue(userId, out var owned) ? owned : Array.Empty<string>();
                var matching = chains
                    .Where(chain => string.IsNullOrWhiteSpace(query.ChainType)
                        || string.Equals(chain, query.ChainType, StringComparison.OrdinalIgnoreCase))
                    .Select(chain => (IWallet)new StubWallet { UserId = userId, ChainType = chain });
                return new AZOAResult<IEnumerable<IWallet>> { Result = matching.ToList() };
            });
        return mock.Object;
    }

    /// <summary>
    /// Stands in for the payout-method store a fiat rail will own. Its existence is the
    /// point: the gate needs no change when that store is built.
    /// </summary>
    private sealed class FakeFiatDestinationDirectory : IRailDestinationDirectory
    {
        private readonly HashSet<Guid> _holders;

        public FakeFiatDestinationDirectory(string railId, IEnumerable<Guid> holders)
        {
            RailId = railId;
            _holders = holders.ToHashSet();
        }

        public string RailId { get; }

        public Task<bool> HasUsableDestinationAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult(_holders.Contains(userId));
    }

    private sealed class ThrowingDirectory : IRailDestinationDirectory
    {
        public ThrowingDirectory(string railId) => RailId = railId;

        public string RailId { get; }

        public Task<bool> HasUsableDestinationAsync(Guid userId, CancellationToken ct = default) =>
            throw new InvalidOperationException("payout directory unavailable");
    }

    private sealed class StubWallet : IWallet
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public string ChainType { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string? PublicKey { get; set; }
        public string? Label { get; set; }
        public bool IsDefault { get; set; }
        public WalletType WalletType { get; set; }
        public string? EncryptedPrivateKey { get; set; }
        public string? EncryptedSeedPhrase { get; set; }
        public DateTime CreatedDate { get; set; }
    }
}
