using System.Text.Json;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// C1/H1 authorization-inversion regression (hardening review). A non-owner who
/// runs another user's PUBLIC action must execute every side-effecting node under
/// the RUNNER's identity (<c>context.ActingUserId</c>), NOT the action owner's
/// (<c>context.ActionDefinition.UserId</c>). The pre-fix bug ran nodes under the owner —
/// cross-user asset theft. See Services/ActionDefinition/AGENTS.md §acting-user.
///
/// These tests exercise the handlers directly with a context whose acting user
/// (runner) is DELIBERATELY different from the action owner, and assert the acting
/// identity that reaches the manager is the runner. They also pin the fail-closed
/// capability gate: it must check the RUNNER's wallet, not the owner's.
/// </summary>
public class ActionActingUserAuthorizationTests
{
    private static readonly Guid Owner  = Guid.NewGuid();
    private static readonly Guid Runner = Guid.NewGuid();

    /// <summary>A action OWNED by <see cref="Owner"/> containing <paramref name="node"/>.</summary>
    private static ActionEntity OwnerActionWith(ActionNode node) =>
        new() { Id = Guid.NewGuid(), UserId = Owner, Nodes = { node } };

    private static ActionNode NodeWith(ActionNodeType type, string config) =>
        new() { Id = Guid.NewGuid(), NodeType = type, Config = config };

    /// <summary>
    /// Marketplace context: the action is owned by <see cref="Owner"/> but the
    /// acting user is <see cref="Runner"/> — the two diverge exactly as they do
    /// when a non-owner starts a PUBLIC action.
    /// </summary>
    private static ActionNodeExecutionContext MarketplaceCtx(ActionNode node) =>
        new(Guid.NewGuid(), node.Id, OwnerActionWith(node), actingUserId: Runner);

    // ─── C1: mutating chain node runs under the RUNNER, never the owner ───

    [Fact]
    public async Task TransferNode_ActsAsRunner_NotActionOwner()
    {
        var nftId = Guid.NewGuid();
        var nft = new Mock<INftManager>();
        Guid? captured = null;
        nft.Setup(m => m.TransferAsync(nftId, It.IsAny<NftTransferRequest>(),
                It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .Callback<Guid, NftTransferRequest, Guid, AZOARequest?, Guid?>(
               (_, _, user, _, _) => captured = user)
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var handler = new TransferNodeHandler(nft.Object);
        var cfg = new TransferNodeConfig { NftId = nftId, Request = new NftTransferRequest { TargetUserId = Guid.NewGuid() } };
        var node = NodeWith(ActionNodeType.Transfer, JsonSerializer.Serialize(cfg));

        var result = await handler.HandleAsync(MarketplaceCtx(node));

        result.IsError.Should().BeFalse();
        // The load-bearing assertion: the acting user is the RUNNER, not the owner.
        captured.Should().Be(Runner);
        captured.Should().NotBe(Owner);
    }

    [Fact]
    public async Task GrantNode_MintsUnderRunner_NotActionOwner()
    {
        var nft = new Mock<INftManager>();
        Guid? captured = null;
        nft.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(),
                It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .Callback<NftMintRequest, Guid, AZOARequest?, Guid?>(
               (_, user, _, _) => captured = user)
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var handler = new GrantNodeHandler(nft.Object, new Mock<IHolonManager>().Object);
        var node = NodeWith(ActionNodeType.Grant,
            JsonSerializer.Serialize(new GrantNodeConfig { Request = new NftMintRequest() }));

        var result = await handler.HandleAsync(MarketplaceCtx(node));

        result.IsError.Should().BeFalse();
        captured.Should().Be(Runner);
        captured.Should().NotBe(Owner);
    }

    // ─── C1: HolonUpdate mutation scopes to the RUNNER (also closes the prior
    //         unscoped-IDOR gap — the handler now passes an user at all) ───

    [Fact]
    public async Task HolonUpdateNode_ScopesToRunner_NotActionOwner()
    {
        var holonId = Guid.NewGuid();
        var holon = new Mock<IHolonManager>();
        Guid? captured = null;
        holon.Setup(m => m.UpdateAsync(holonId, It.IsAny<HolonUpdateModel>(),
                It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
             .Callback<Guid, HolonUpdateModel, Guid?, AZOARequest?>(
                 (_, _, user, _) => captured = user)
             .ReturnsAsync(new AZOAResult<IHolon> { Result = new Holon { Id = holonId } });

        var handler = new HolonUpdateNodeHandler(holon.Object);
        var cfg = new HolonUpdateNodeConfig { HolonId = holonId, Model = new HolonUpdateModel() };
        var node = NodeWith(ActionNodeType.HolonUpdate, JsonSerializer.Serialize(cfg));

        var result = await handler.HandleAsync(MarketplaceCtx(node));

        result.IsError.Should().BeFalse();
        // Scoped to the runner ⇒ the manager's ownership guard rejects a holon
        // owned by the action owner. Never null (the pre-fix unscoped call).
        captured.Should().Be(Runner);
        captured.Should().NotBeNull();
        captured.Should().NotBe(Owner);
    }

    // ─── C1: fail closed when the RUNNER lacks the required binding ───

    [Fact]
    public async Task CapabilityGate_ChecksRunnerWallet_FailsClosedWhenRunnerHasNone()
    {
        // The OWNER has a bound wallet; the RUNNER has none. The gate must key off
        // the runner and fail closed — a marketplace runner cannot borrow the
        // owner's chain capability.
        var wallets = new Mock<IWalletManager>();
        wallets.Setup(m => m.QueryAsync(It.IsAny<WalletQueryRequest>(), Owner, It.IsAny<AZOARequest?>()))
               .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>>
               { Result = new IWallet[] { new StubWallet() } });
        wallets.Setup(m => m.QueryAsync(It.IsAny<WalletQueryRequest>(), Runner, It.IsAny<AZOARequest?>()))
               .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = Array.Empty<IWallet>() });

        var gate = new ValueRailCapabilityGate(wallets.Object, defaultRailId: "Algorand");

        // The runner has no wallet ⇒ gate fails closed.
        var runnerBound = await gate.HasUsableDestinationAsync(Runner, railId: null);
        runnerBound.Should().BeFalse();

        // Sanity: the owner WOULD pass — proving the gate is identity-sensitive and
        // the pre-fix code (checking the owner) would have wrongly let the run
        // broadcast under the owner's key.
        var ownerBound = await gate.HasUsableDestinationAsync(Owner, railId: null);
        ownerBound.Should().BeTrue();
    }

    private sealed class StubWallet : IWallet
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; } = Owner;
        public string ChainType { get; set; } = "Algorand";
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
