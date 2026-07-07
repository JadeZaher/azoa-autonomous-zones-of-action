using System.Text.Json;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Handlers;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// C1/H1 authorization-inversion regression (hardening review). A non-owner who
/// runs another avatar's PUBLIC quest must execute every side-effecting node under
/// the RUNNER's identity (<c>context.ActingAvatarId</c>), NOT the quest owner's
/// (<c>context.Quest.AvatarId</c>). The pre-fix bug ran nodes under the owner —
/// cross-avatar asset theft. See Services/Quest/AGENTS.md §acting-avatar.
///
/// These tests exercise the handlers directly with a context whose acting avatar
/// (runner) is DELIBERATELY different from the quest owner, and assert the acting
/// identity that reaches the manager is the runner. They also pin the fail-closed
/// capability gate: it must check the RUNNER's wallet, not the owner's.
/// </summary>
public class QuestActingAvatarAuthorizationTests
{
    private static readonly Guid Owner  = Guid.NewGuid();
    private static readonly Guid Runner = Guid.NewGuid();

    /// <summary>A quest OWNED by <see cref="Owner"/> containing <paramref name="node"/>.</summary>
    private static QuestEntity OwnerQuestWith(QuestNode node) =>
        new() { Id = Guid.NewGuid(), AvatarId = Owner, Nodes = { node } };

    private static QuestNode NodeWith(QuestNodeType type, string config) =>
        new() { Id = Guid.NewGuid(), NodeType = type, Config = config };

    /// <summary>
    /// Marketplace context: the quest is owned by <see cref="Owner"/> but the
    /// acting avatar is <see cref="Runner"/> — the two diverge exactly as they do
    /// when a non-owner starts a PUBLIC quest.
    /// </summary>
    private static QuestNodeExecutionContext MarketplaceCtx(QuestNode node) =>
        new(Guid.NewGuid(), node.Id, OwnerQuestWith(node), actingAvatarId: Runner);

    // ─── C1: mutating chain node runs under the RUNNER, never the owner ───

    [Fact]
    public async Task TransferNode_ActsAsRunner_NotQuestOwner()
    {
        var nftId = Guid.NewGuid();
        var nft = new Mock<INftManager>();
        Guid? captured = null;
        nft.Setup(m => m.TransferAsync(nftId, It.IsAny<NftTransferRequest>(),
                It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .Callback<Guid, NftTransferRequest, Guid, AZOARequest?, Guid?>(
               (_, _, avatar, _, _) => captured = avatar)
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var handler = new TransferNodeHandler(nft.Object);
        var cfg = new TransferNodeConfig { NftId = nftId, Request = new NftTransferRequest { TargetAvatarId = Guid.NewGuid() } };
        var node = NodeWith(QuestNodeType.Transfer, JsonSerializer.Serialize(cfg));

        var result = await handler.HandleAsync(MarketplaceCtx(node));

        result.IsError.Should().BeFalse();
        // The load-bearing assertion: the acting avatar is the RUNNER, not the owner.
        captured.Should().Be(Runner);
        captured.Should().NotBe(Owner);
    }

    [Fact]
    public async Task GrantNode_MintsUnderRunner_NotQuestOwner()
    {
        var nft = new Mock<INftManager>();
        Guid? captured = null;
        nft.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(),
                It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .Callback<NftMintRequest, Guid, AZOARequest?, Guid?>(
               (_, avatar, _, _) => captured = avatar)
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var handler = new GrantNodeHandler(nft.Object, new Mock<IHolonManager>().Object);
        var node = NodeWith(QuestNodeType.Grant,
            JsonSerializer.Serialize(new GrantNodeConfig { Request = new NftMintRequest() }));

        var result = await handler.HandleAsync(MarketplaceCtx(node));

        result.IsError.Should().BeFalse();
        captured.Should().Be(Runner);
        captured.Should().NotBe(Owner);
    }

    // ─── C1: HolonUpdate mutation scopes to the RUNNER (also closes the prior
    //         unscoped-IDOR gap — the handler now passes an avatar at all) ───

    [Fact]
    public async Task HolonUpdateNode_ScopesToRunner_NotQuestOwner()
    {
        var holonId = Guid.NewGuid();
        var holon = new Mock<IHolonManager>();
        Guid? captured = null;
        holon.Setup(m => m.UpdateAsync(holonId, It.IsAny<HolonUpdateModel>(),
                It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
             .Callback<Guid, HolonUpdateModel, Guid?, AZOARequest?>(
                 (_, _, avatar, _) => captured = avatar)
             .ReturnsAsync(new AZOAResult<IHolon> { Result = new Holon { Id = holonId } });

        var handler = new HolonUpdateNodeHandler(holon.Object);
        var cfg = new HolonUpdateNodeConfig { HolonId = holonId, Model = new HolonUpdateModel() };
        var node = NodeWith(QuestNodeType.HolonUpdate, JsonSerializer.Serialize(cfg));

        var result = await handler.HandleAsync(MarketplaceCtx(node));

        result.IsError.Should().BeFalse();
        // Scoped to the runner ⇒ the manager's ownership guard rejects a holon
        // owned by the quest owner. Never null (the pre-fix unscoped call).
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

        // The runner has no wallet ⇒ gate fails closed.
        var runnerBound = await ChainCapabilityGate.HasWalletBoundAsync(wallets.Object, Runner);
        runnerBound.Should().BeFalse();

        // Sanity: the owner WOULD pass — proving the gate is identity-sensitive and
        // the pre-fix code (checking the owner) would have wrongly let the run
        // broadcast under the owner's key.
        var ownerBound = await ChainCapabilityGate.HasWalletBoundAsync(wallets.Object, Owner);
        ownerBound.Should().BeTrue();
    }

    private sealed class StubWallet : IWallet
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid AvatarId { get; set; } = Owner;
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
