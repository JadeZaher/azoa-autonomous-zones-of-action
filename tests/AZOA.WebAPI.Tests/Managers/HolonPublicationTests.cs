// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;
using Xunit;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// The publication seam: <c>IsPublic</c> is what <c>HolonManager.CanRead</c> reads,
/// so it is the difference between "my holon" and "every caller on the node's holon".
/// </summary>
/// <remarks>
/// Two things are pinned here and they pull in opposite directions on purpose.
/// A governance proposal MUST be publishable — an unpublished proposal is one only
/// its author can vote on, which is the whole governance primitive being inert (G2).
/// Everything else MUST NOT be, because this repo has leaked cross-tenant through
/// generic read surfaces three times and a free <c>isPublic: true</c> on
/// <c>POST /api/holon</c> is that leak with a nicer name. Ballots are on the wrong
/// side of the line deliberately: a public ballot carries <c>Holon.UserId</c> beside
/// the choice.
/// </remarks>
public class HolonPublicationTests
{
    private static readonly Guid Owner = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Member = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly Mock<IHolonStore> _store = new();
    private readonly HolonManager _manager;

    public HolonPublicationTests()
    {
        _store.Setup(s => s.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IHolon holon, CancellationToken _) => new AZOAResult<IHolon> { Result = holon });
        _manager = new HolonManager(_store.Object);
    }

    private Holon Existing(string? assetType, bool isPublic = false)
    {
        var holon = new Holon
        {
            Id = Guid.NewGuid(),
            Name = "Existing",
            UserId = Owner,
            AssetType = assetType,
            IsPublic = isPublic,
        };
        _store.Setup(s => s.GetByIdAsync(holon.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IHolon> { Result = holon });
        return holon;
    }

    [Fact]
    public async Task CreateAsync_PublishesAGovernanceProposal_WhenAskedTo()
    {
        var result = await _manager.CreateAsync(
            new HolonCreateModel
            {
                Name = "Budget",
                AssetType = ConsumerGovernanceHolonTypes.ProposalAssetType,
                IsPublic = true,
            },
            Owner);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.IsPublic.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_RefusesToPublishAnOrdinaryHolon_AndWritesNothing()
    {
        var result = await _manager.CreateAsync(
            new HolonCreateModel { Name = "Treasure", AssetType = "NFT", IsPublic = true },
            Owner);

        // A refusal, not a silent private write: a caller that believed it published
        // would hand out a link nobody else can open.
        result.IsError.Should().BeTrue();
        _store.Verify(s => s.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_RefusesToPublishABallot()
    {
        var result = await _manager.CreateAsync(
            new HolonCreateModel
            {
                Name = "Ballot",
                AssetType = ConsumerGovernanceHolonTypes.VoteAssetType,
                IsPublic = true,
            },
            Owner);

        result.IsError.Should().BeTrue("a public ballot reconstitutes the person-to-choice correlation");
    }

    [Fact]
    public async Task CreateAsync_LeavesEverythingElsePrivate_ByDefault()
    {
        var result = await _manager.CreateAsync(new HolonCreateModel { Name = "Plain" }, Owner);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.IsPublic.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_RefusesToPublishAnOrdinaryHolon()
    {
        var holon = Existing("NFT");

        var result = await _manager.UpdateAsync(holon.Id, new HolonUpdateModel { IsPublic = true }, Owner);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_AllowsUnpublishing()
    {
        var proposal = Existing(ConsumerGovernanceHolonTypes.ProposalAssetType, isPublic: true);

        var result = await _manager.UpdateAsync(proposal.Id, new HolonUpdateModel { IsPublic = false }, Owner);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.IsPublic.Should().BeFalse();
    }

    /// <summary>
    /// The check runs on the POST-MERGE state, so publication cannot be carried over
    /// to a type that may not hold it by renaming the AssetType afterwards.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_RefusesToCarryPublicationOntoAnUnpublishableAssetType()
    {
        var proposal = Existing(ConsumerGovernanceHolonTypes.ProposalAssetType, isPublic: true);

        var result = await _manager.UpdateAsync(proposal.Id, new HolonUpdateModel { AssetType = "NFT" }, Owner);

        result.IsError.Should().BeTrue();
    }

    /// <summary>
    /// G2 end to end at the manager: this read is the one <c>GovernanceVote</c>
    /// performs as the RUNNER, and it is what an unpublished proposal fails.
    /// </summary>
    [Fact]
    public async Task GetAsync_ByAMemberWhoIsNotTheAuthor_ReturnsAPublishedProposal()
    {
        var published = Existing(ConsumerGovernanceHolonTypes.ProposalAssetType, isPublic: true);
        var unpublished = Existing(ConsumerGovernanceHolonTypes.ProposalAssetType);

        (await _manager.GetAsync(published.Id, Member)).IsError.Should().BeFalse();
        (await _manager.GetAsync(unpublished.Id, Member)).IsError.Should().BeTrue();
    }
}
