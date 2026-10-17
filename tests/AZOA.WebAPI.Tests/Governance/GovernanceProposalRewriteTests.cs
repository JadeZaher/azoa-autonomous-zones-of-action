using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Governance;

public sealed class GovernanceProposalRewriteTests
{
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Holon _proposal = new() { Id = Guid.NewGuid(), Name = "Original", Description = "Question",
        AssetType = ConsumerGovernanceHolonTypes.ProposalAssetType, IsPublic = true,
        Metadata = new() { ["status"] = "Open", ["electorateTenant"] = Guid.NewGuid().ToString("N") } };

    private HolonManager Manager(out Mock<IHolonStore> store)
    {
        _proposal.UserId = _owner;
        store = new Mock<IHolonStore>(MockBehavior.Strict);
        store.Setup(value => value.GetByIdAsync(_proposal.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IHolon>.Success(_proposal));
        return new HolonManager(store.Object);
    }

    [Theory]
    [InlineData("options")]
    [InlineData("uniquenessScope")]
    [InlineData("uniquenessOperationKey")]
    [InlineData("electorateTenant")]
    [InlineData("opensAt")]
    [InlineData("closesAt")]
    [InlineData("status")]
    [InlineData("definitionHash")]
    public async Task Proposal_metadata_is_immutable_even_before_first_vote(string key)
    {
        var manager = Manager(out var store);
        var result = await manager.UpdateAsync(_proposal.Id, new HolonUpdateModel { Metadata = new() { [key] = "forged" } }, _owner);
        result.IsError.Should().BeTrue();
        store.Verify(value => value.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Question_publication_type_and_deletion_cannot_change_authoritative_projection()
    {
        var manager = Manager(out var store);
        foreach (var edit in new[] { new HolonUpdateModel { Name = "Replacement" }, new HolonUpdateModel { Description = "Replacement" },
            new HolonUpdateModel { IsPublic = false }, new HolonUpdateModel { AssetType = "Ordinary" } })
            (await manager.UpdateAsync(_proposal.Id, edit, _owner)).IsError.Should().BeTrue();
        (await manager.DeleteAsync(_proposal.Id, _owner)).IsError.Should().BeTrue();
        store.Verify(value => value.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(value => value.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handwritten_election_creation_is_refused_before_storage()
    {
        var manager = Manager(out var store);
        (await manager.CreateAsync(new HolonCreateModel { Name = "Forged", AssetType = ConsumerGovernanceHolonTypes.ProposalAssetType,
            Metadata = new() { ["electorateTenant"] = _owner.ToString("N") } }, _owner)).IsError.Should().BeTrue();
        store.Verify(value => value.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Interaction_and_direct_clone_cannot_change_or_copy_an_election()
    {
        var manager = Manager(out var store);
        foreach (var request in new[] { new HolonInteractionRequest { SetMetadata = new() { ["options"] = "forged" } },
            new HolonInteractionRequest { RemoveMetadataKeys = ["status"] },
            new HolonInteractionRequest { NewParentHolonId = Guid.NewGuid() },
            new HolonInteractionRequest { AddPeerHolonIds = [Guid.NewGuid()] } })
            (await manager.InteractAsync(_proposal.Id, request, _owner)).IsError.Should().BeTrue();
        (await manager.CloneAsync(_proposal.Id, new HolonCloneRequest { IncludeSubtree = true }, _owner)).IsError.Should().BeTrue();
        store.Verify(value => value.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
