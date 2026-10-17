using FluentAssertions;
using Xunit;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Tests.Actions.Handlers;

public class GovernanceNodeHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    [Fact]
    public void TallyHash_IsOrderStable()
    {
        var proposalId = Guid.NewGuid();
        var ascending = new Dictionary<string, int> { ["abstain"] = 0, ["no"] = 1, ["yes"] = 2 };
        var shuffled = new Dictionary<string, int> { ["yes"] = 2, ["abstain"] = 0, ["no"] = 1 };

        GovernanceTallyHash.Compute(proposalId, "2026-09-06T00:00:00.0000000+00:00", 3, ascending)
            .Should().Be(GovernanceTallyHash.Compute(proposalId, "2026-09-06T00:00:00.0000000+00:00", 3, shuffled));
    }

    [Fact]
    public void TallyHash_LengthPrefixedOptions_CannotForgeTheSameDigest()
    {
        var proposalId = Guid.NewGuid();

        // Without the length prefix, an option that embeds the separators could serialize
        // to the same bytes as two separate options.
        var honest = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
        var forged = new Dictionary<string, int> { ["a=1\n1:b"] = 2 };

        GovernanceTallyHash.Compute(proposalId, null, 3, honest)
            .Should().NotBe(GovernanceTallyHash.Compute(proposalId, null, 3, forged));
    }

    [Fact]
    public void TallyHash_ChangesWithTheCloseTime()
    {
        var proposalId = Guid.NewGuid();
        var counts = new Dictionary<string, int> { ["yes"] = 1 };

        GovernanceTallyHash.Compute(proposalId, "2026-09-06T00:00:00.0000000+00:00", 1, counts)
            .Should().NotBe(GovernanceTallyHash.Compute(proposalId, "2026-09-07T00:00:00.0000000+00:00", 1, counts));
    }

    // ── registry seed ─────────────────────────────────────────────────────────

    [Fact]
    public void SeedRegistrations_ConstrainBothAssetTypes_WithoutRequiringTheDerivedKey()
    {
        var seeds = ConsumerGovernanceHolonTypes.SeedRegistrations(Now);

        seeds.Select(s => s.AssetType).Should().BeEquivalentTo(
            ConsumerGovernanceHolonTypes.ProposalAssetType,
            ConsumerGovernanceHolonTypes.VoteAssetType);

        var proposalSeed = seeds.Single(s => s.AssetType == ConsumerGovernanceHolonTypes.ProposalAssetType);
        proposalSeed.RequiredMetadataFields.Should().Contain(ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessScope);

        // uniquenessOperationKey is derived from the proposal's own id, so it is
        // recorded a beat after creation and cannot be required at create time.
        proposalSeed.RequiredMetadataFields
            .Should().NotContain(ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey);

        var voteSeed = seeds.Single(s => s.AssetType == ConsumerGovernanceHolonTypes.VoteAssetType);
        voteSeed.RequiredMetadataFields.Should().BeEquivalentTo(
            ConsumerGovernanceHolonTypes.VoteMetadata.Option,
            ConsumerGovernanceHolonTypes.VoteMetadata.CastAt);
    }
}
