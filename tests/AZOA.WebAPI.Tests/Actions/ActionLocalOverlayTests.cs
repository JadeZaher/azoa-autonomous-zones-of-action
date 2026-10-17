using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class ActionLocalOverlayTests
{
    [Fact]
    public async Task MutatingManagerReadsCannotChangeOriginalCommitWitness()
    {
        var source = new Mock<IHolonStore>(MockBehavior.Strict);
        var original = new Holon { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "original" };
        source.Setup(store => store.GetByIdAsync(original.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IHolon>.Success(original));
        var plan = new ActionLocalMutationPlan();
        var overlay = new ActionLocalHolonOverlay(source.Object, plan);
        var read = await overlay.GetByIdAsync(original.Id);
        read.Result!.Name = "changed";
        await overlay.UpsertAsync(read.Result);

        plan.HolonReads[original.Id]!.Name.Should().Be("original");
        plan.HolonWrites[original.Id]!.Name.Should().Be("changed");
        original.Name.Should().Be("original");
        source.Verify(store => store.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReservedProposalInSubtreeRefusesWholePlanIncludingEarlierStagedWrites()
    {
        var actor = Guid.NewGuid();
        var root = new Holon { Id = Guid.NewGuid(), UserId = actor, Name = "root" };
        var proposal = new Holon { Id = Guid.NewGuid(), UserId = actor, ParentHolonId = root.Id,
            AssetType = ConsumerGovernanceHolonTypes.ProposalAssetType };
        var source = new Mock<IHolonStore>(MockBehavior.Strict);
        source.Setup(store => store.GetByIdAsync(root.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IHolon>.Success(root));
        source.Setup(store => store.QueryAsync(It.IsAny<HolonQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<IHolon>>.Success([proposal]));
        var plan = new ActionLocalMutationPlan();
        var overlay = new ActionLocalHolonOverlay(source.Object, plan);
        var read = await overlay.GetByIdAsync(root.Id);
        read.Result!.Name = "staged";
        await overlay.UpsertAsync(read.Result);
        var descendants = await overlay.QueryAsync(new HolonQueryRequest { ParentHolonId = root.Id });

        descendants.IsError.Should().BeTrue();
        plan.IsWithinBounds().Should().BeFalse();
        root.Name.Should().Be("root");
        source.Verify(store => store.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueryWitnessContainsOriginalMembershipWhileManagerSeesStagedChanges()
    {
        var actor = Guid.NewGuid();
        var first = new Wallet { Id = Guid.NewGuid(), UserId = actor, IsDefault = true };
        var source = new Mock<IWalletStore>(MockBehavior.Strict);
        source.Setup(store => store.GetByUserAsync(actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<IWallet>>.Success([first]));
        var plan = new ActionLocalMutationPlan();
        var overlay = new ActionLocalWalletOverlay(source.Object, plan);
        var read = await overlay.GetByUserAsync(actor);
        var staged = read.Result!.Single();
        staged.IsDefault = false;
        await overlay.UpsertAsync(staged);
        var secondRead = await overlay.GetByUserAsync(actor);

        secondRead.Result!.Single().IsDefault.Should().BeFalse();
        plan.WalletReads[first.Id]!.IsDefault.Should().BeTrue();
        plan.WalletQueries.Should().OnlyContain(query => query.Ids.SequenceEqual(new[] { first.Id }));
        first.IsDefault.Should().BeTrue();
        source.Verify(store => store.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
