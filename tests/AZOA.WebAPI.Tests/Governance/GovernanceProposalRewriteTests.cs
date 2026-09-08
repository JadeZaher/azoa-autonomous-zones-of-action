// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Xunit;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Tests.Governance;

/// <summary>
/// W9.2 — the PROPOSAL-rewrite attack tests. The D39 lane proved a forged ballot counts
/// for nothing; this file asks the other half of the question: can the author move the
/// outcome without touching a single ballot?
/// </summary>
/// <remarks>
/// <para>
/// The route is ordinary and needs no privilege: the proposal holon is owned by whoever
/// ran <c>GovernanceProposalCreate</c>, <c>PUT /api/holon/{id}</c> checks ownership and
/// then blind-merges metadata, and everything a recorded vote depends on — the declared
/// options, the uniqueness scope, the electorate, the window, the derived uniqueness key
/// — lives in that metadata. Rewriting the scope was the sharpest: every claim already
/// recorded moves out of the domain the tally counts in, so the votes silently vanish
/// while <c>tallyHash</c> still verifies against the numbers it was given.
/// </para>
/// <para>
/// Every assertion here reads an OUTCOME — the published tally, or whether the platform
/// accepted the write — never a mechanism, so the same tests survive a move to the
/// alternative fix (snapshotting the question onto the claim rows) and go red again the
/// day the freeze is relaxed. See <c>Services/Governance/AGENTS.md</c> §proposal-freeze.
/// </para>
/// </remarks>
public class GovernanceProposalRewriteTests
{
    /// <summary>Sentence the freeze refuses with; asserted by prefix so wording can settle.</summary>
    private const string FrozenPrefix = "A governance proposal is frozen once its members start voting";

    /// <summary>Sentence the electorate rule refuses with.</summary>
    private const string ElectoratePrefix = "A governance proposal's electorate must name one tenant";

    /// <summary>Refusal a caller sees for a proposal they may not read, or one that never existed.</summary>
    private const string ProposalUnreadable = "governance proposal not found.";

    // ── the question ────────────────────────────────────────────────────────

    /// <summary>
    /// The author replaces the option list after a vote is recorded. Counting is keyed by
    /// declared option, so a de-listed option is a vote that is no longer counted for
    /// anything — the outcome moves and no ballot was touched.
    /// </summary>
    [Fact]
    public async Task Proposal_OptionsCannotBeRewritten_AfterTheFirstVoteOfRecord()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var member = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        (await world.VoteAsync(member, proposal, "yes")).IsError.Should().BeFalse();

        var rewrite = await world.RewriteProposalAsync(
            proposal,
            author,
            new Dictionary<string, string>
            {
                [ConsumerGovernanceHolonTypes.ProposalMetadata.Options] =
                    ConsumerGovernanceHolonTypes.SerializeOptions(new[] { "maybe", "later" }),
            });

        rewrite.IsError.Should().BeTrue();
        rewrite.Message.Should().StartWith(FrozenPrefix);

        var tally = GovernanceWorld.ReadTally(await world.TallyAsync(author, proposal));
        tally.TotalVotes.Should().Be(1);
        tally.Counts["yes"].Should().Be(1);
    }

    // ── the domain the votes live in ────────────────────────────────────────

    /// <summary>
    /// The sharpest rewrite: moving <c>uniquenessScope</c> from Tenant to Global moves the
    /// domain the tally counts in, so every recorded claim disappears from the count while
    /// the published hash still verifies against the numbers it was handed.
    /// </summary>
    [Fact]
    public async Task Proposal_UniquenessScopeCannotBeRewritten_SoRecordedVotesAreNotDroppedFromTheCount()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var member = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        (await world.VoteAsync(member, proposal, "yes")).IsError.Should().BeFalse();

        var rewrite = await world.RewriteProposalAsync(
            proposal,
            author,
            new Dictionary<string, string>
            {
                [ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessScope] = "Global",
            });

        rewrite.IsError.Should().BeTrue();
        rewrite.Message.Should().StartWith(FrozenPrefix);

        var tally = GovernanceWorld.ReadTally(await world.TallyAsync(author, proposal));
        tally.TotalVotes.Should().Be(1, "the recorded vote is still counted in the domain it was claimed in");
    }

    /// <summary>
    /// Re-pointing the electorate does the same damage from the other side: the tally
    /// derives its scope from the RECORDED electorate, so a new electorate is a new,
    /// empty domain — and a different set of people admitted to it.
    /// </summary>
    [Fact]
    public async Task Proposal_ElectorateCannotBeRewritten_AfterTheFirstVoteOfRecord()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var member = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        (await world.VoteAsync(member, proposal, "yes")).IsError.Should().BeFalse();

        var rewrite = await world.RewriteProposalAsync(
            proposal,
            author,
            new Dictionary<string, string>
            {
                [ConsumerGovernanceHolonTypes.ProposalMetadata.ElectorateTenant] =
                    ConsumerGovernanceHolonTypes.ElectorateValueFor(Guid.NewGuid()),
            });

        rewrite.IsError.Should().BeTrue();
        rewrite.Message.Should().StartWith(FrozenPrefix);

        GovernanceWorld.ReadTally(await world.TallyAsync(author, proposal)).TotalVotes.Should().Be(1);
    }

    /// <summary>
    /// The derived uniqueness key is the last coordinate of the domain. It was already
    /// protected by the derived-key equality check on both governance nodes; the freeze
    /// refuses the write itself, so a proposal cannot be left pointing at a domain nobody
    /// claimed in even for the length of one request.
    /// </summary>
    [Fact]
    public async Task Proposal_UniquenessKeyCannotBeRewritten_AfterTheFirstVoteOfRecord()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var member = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        (await world.VoteAsync(member, proposal, "yes")).IsError.Should().BeFalse();

        var rewrite = await world.RewriteProposalAsync(
            proposal,
            author,
            new Dictionary<string, string>
            {
                [ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey] =
                    ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(Guid.NewGuid()),
            });

        rewrite.IsError.Should().BeTrue();
        GovernanceWorld.ReadTally(await world.TallyAsync(author, proposal)).TotalVotes.Should().Be(1);
    }

    // ── the window ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>decision_closes_at</c> is pinned on a claim row, so extending <c>closesAt</c>
    /// cannot thaw a decision already recorded — but it CAN let in a voter who has no row
    /// yet, which is a fresh vote after the poll the members were shown had closed.
    /// </summary>
    [Fact]
    public async Task Proposal_ClosesAtCannotBeExtended_AfterTheFirstVoteOfRecord()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var member = world.VerifiedUser();
        var latecomer = world.VerifiedUser();
        var closesAt = GovernanceWorld.Start.AddHours(1);
        var proposal = await world.CreateProposalAsync(author, new[] { "yes", "no" }, closesAt);

        (await world.VoteAsync(member, proposal, "yes")).IsError.Should().BeFalse();
        world.Clock.Advance(TimeSpan.FromHours(2));

        var rewrite = await world.RewriteProposalAsync(
            proposal,
            author,
            new Dictionary<string, string>
            {
                [ConsumerGovernanceHolonTypes.ProposalMetadata.ClosesAt] =
                    GovernanceWorld.Start.AddDays(30).ToString("O"),
            });

        rewrite.IsError.Should().BeTrue();
        rewrite.Message.Should().StartWith(FrozenPrefix);

        (await world.VoteAsync(latecomer, proposal, "no")).IsError.Should()
            .BeTrue("the poll the members were shown has closed and the author may not reopen it");
        world.LiveClaimsOn(proposal).Should().HaveCount(1);
    }

    /// <summary>
    /// The mirror image: closing early. Flipping <c>status</c> to Closed once one member
    /// has voted shuts the poll on everyone who has not yet had their say.
    /// </summary>
    [Fact]
    public async Task Proposal_CannotBeClosedEarly_AfterTheFirstVoteOfRecord()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var early = world.VerifiedUser();
        var late = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        (await world.VoteAsync(early, proposal, "yes")).IsError.Should().BeFalse();

        var rewrite = await world.RewriteProposalAsync(
            proposal,
            author,
            new Dictionary<string, string>
            {
                [ConsumerGovernanceHolonTypes.ProposalMetadata.Status] =
                    ConsumerGovernanceHolonTypes.StatusClosed,
            });

        rewrite.IsError.Should().BeTrue();
        (await world.VoteAsync(late, proposal, "no")).IsError.Should()
            .BeFalse("a member who had not voted yet keeps the window the notice promised");

        var tally = GovernanceWorld.ReadTally(await world.TallyAsync(author, proposal));
        tally.TotalVotes.Should().Be(2);
    }

    // ── the notice itself ───────────────────────────────────────────────────

    /// <summary>
    /// Un-publishing is the quiet version of the same attack: <c>CanRead</c> is
    /// owner-or-public and the tally reads the proposal through it, so an unpublished
    /// proposal is one only its author can count.
    /// </summary>
    [Fact]
    public async Task Proposal_CannotBeUnpublished_AfterTheFirstVoteOfRecord()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var member = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        (await world.VoteAsync(member, proposal, "yes")).IsError.Should().BeFalse();

        var rewrite = await world.RewriteProposalAsync(proposal, author, isPublic: false);

        rewrite.IsError.Should().BeTrue();
        rewrite.Message.Should().StartWith(FrozenPrefix);

        var byAMember = GovernanceWorld.ReadTally(await world.TallyAsync(member, proposal));
        byAMember.TotalVotes.Should().Be(1, "the electorate can still read the result it produced");
    }

    /// <summary>
    /// Renaming the asset type makes every recorded vote unreachable: both governance
    /// nodes check the type before anything else, so the proposal stops being a proposal.
    /// </summary>
    [Fact]
    public async Task Proposal_AssetTypeCannotBeRenamed_AfterTheFirstVoteOfRecord()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var member = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        (await world.VoteAsync(member, proposal, "yes")).IsError.Should().BeFalse();

        var rewrite = await world.RewriteProposalAsync(proposal, author, assetType: "NFT");

        rewrite.IsError.Should().BeTrue();
        rewrite.Message.Should().StartWith(FrozenPrefix);
        world.ProposalHolon(proposal).AssetType
            .Should().Be(ConsumerGovernanceHolonTypes.ProposalAssetType);
    }

    /// <summary>
    /// Deleting is the bluntest rewrite. The votes of record survive in the claim ledger,
    /// so the result is a decision nobody can ever read back rather than one anybody can
    /// dispute.
    /// </summary>
    [Fact]
    public async Task Proposal_CannotBeDeleted_AfterTheFirstVoteOfRecord()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var member = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        (await world.VoteAsync(member, proposal, "yes")).IsError.Should().BeFalse();

        var deleted = await world.DeleteProposalAsync(proposal, author);

        deleted.IsError.Should().BeTrue();
        deleted.Message.Should().StartWith(FrozenPrefix);
        GovernanceWorld.ReadTally(await world.TallyAsync(author, proposal)).TotalVotes.Should().Be(1);
    }

    // ── the second mutation seam ────────────────────────────────────────────

    /// <summary>
    /// <c>InteractAsync</c> sets arbitrary metadata on any holon its caller owns. A freeze
    /// enforced only on the update route would ship with a documented way around it —
    /// which is this repo's recurring defect wearing a different hat.
    /// </summary>
    [Fact]
    public async Task Proposal_CannotBeRewrittenThroughTheInteractionSeam_AfterTheFirstVoteOfRecord()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var member = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        (await world.VoteAsync(member, proposal, "yes")).IsError.Should().BeFalse();

        var interaction = await world.InteractWithProposalAsync(
            proposal,
            author,
            setMetadata: new Dictionary<string, string>
            {
                [ConsumerGovernanceHolonTypes.ProposalMetadata.Options] =
                    ConsumerGovernanceHolonTypes.SerializeOptions(new[] { "maybe", "later" }),
            });

        interaction.IsError.Should().BeTrue();
        interaction.Message.Should().StartWith(FrozenPrefix);
        GovernanceWorld.ReadTally(await world.TallyAsync(author, proposal)).TotalVotes.Should().Be(1);
    }

    /// <summary>
    /// The same seam DELETES keys, and a deletion is invisible to a value comparison.
    /// Dropping <c>closesAt</c> leaves a poll that never closes, so members who have not
    /// voted keep arriving after the notice said it was over.
    /// </summary>
    [Fact]
    public async Task Proposal_CloseTimeCannotBeDeletedThroughTheInteractionSeam_AfterTheFirstVoteOfRecord()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var member = world.VerifiedUser();
        var latecomer = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddHours(1));

        (await world.VoteAsync(member, proposal, "yes")).IsError.Should().BeFalse();
        world.Clock.Advance(TimeSpan.FromHours(2));

        var interaction = await world.InteractWithProposalAsync(
            proposal,
            author,
            removeMetadataKeys: new[] { ConsumerGovernanceHolonTypes.ProposalMetadata.ClosesAt });

        interaction.IsError.Should().BeTrue();
        interaction.Message.Should().StartWith(FrozenPrefix);
        (await world.VoteAsync(latecomer, proposal, "no")).IsError.Should()
            .BeTrue("the close time the members were shown is still there");
    }

    // ── the freeze is a freeze, not a write lock ────────────────────────────

    /// <summary>
    /// Before anybody votes the author still owns the question outright. A freeze that
    /// blocked ordinary drafting would be routed around within a week.
    /// </summary>
    [Fact]
    public async Task Proposal_QuestionIsStillEditable_BeforeTheFirstVoteOfRecord()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        var rewrite = await world.RewriteProposalAsync(
            proposal,
            author,
            new Dictionary<string, string>
            {
                [ConsumerGovernanceHolonTypes.ProposalMetadata.Options] =
                    ConsumerGovernanceHolonTypes.SerializeOptions(new[] { "yes", "no", "abstain" }),
            });

        rewrite.IsError.Should().BeFalse(rewrite.Message);
    }

    /// <summary>Fixing a typo in the title moves no outcome, so it stays allowed.</summary>
    [Fact]
    public async Task Proposal_TitleStaysEditable_AfterTheFirstVoteOfRecord()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var member = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        (await world.VoteAsync(member, proposal, "yes")).IsError.Should().BeFalse();

        var renamed = await world.RewriteProposalAsync(proposal, author, name: "Budget 2027 (corrected)");

        renamed.IsError.Should().BeFalse(renamed.Message);
        world.ProposalHolon(proposal).Name.Should().Be("Budget 2027 (corrected)");
    }

    /// <summary>
    /// Re-sending exactly what is already recorded is not a rewrite, so a retried request
    /// is not turned into a refusal.
    /// </summary>
    [Fact]
    public async Task Proposal_ResendingTheSameQuestion_IsNotARewrite()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var member = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        (await world.VoteAsync(member, proposal, "yes")).IsError.Should().BeFalse();

        var recorded = world.ProposalHolon(proposal).Metadata;
        var replay = await world.RewriteProposalAsync(
            proposal,
            author,
            new Dictionary<string, string>
            {
                [ConsumerGovernanceHolonTypes.ProposalMetadata.Options] =
                    recorded[ConsumerGovernanceHolonTypes.ProposalMetadata.Options],
                [ConsumerGovernanceHolonTypes.ProposalMetadata.Status] =
                    recorded[ConsumerGovernanceHolonTypes.ProposalMetadata.Status],
            });

        replay.IsError.Should().BeFalse(replay.Message);
    }

    // ── the freeze fails CLOSED, never quiet ────────────────────────────────

    /// <summary>
    /// A rewrite the platform cannot prove is unclaimed is refused. The alternative —
    /// allowing it because the ledger was unreachable — is a control that switches itself
    /// off exactly when the store is in trouble.
    /// </summary>
    [Fact]
    public async Task Proposal_RewriteIsRefused_WhenTheVoteOfRecordLedgerCannotBeRead()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        world.Claims.ListFailure = "the ledger is unreachable";

        var rewrite = await world.RewriteProposalAsync(
            proposal,
            author,
            new Dictionary<string, string>
            {
                [ConsumerGovernanceHolonTypes.ProposalMetadata.Options] =
                    ConsumerGovernanceHolonTypes.SerializeOptions(new[] { "maybe", "later" }),
            });

        rewrite.IsError.Should().BeTrue();
        rewrite.Message.Should().Contain("vote-of-record ledger is unavailable");
    }

    /// <summary>
    /// The dependency being ABSENT is the version of this defect the repo has actually
    /// shipped six times: a control that quietly does nothing because nothing was wired
    /// into it. An unwired ledger is a refusal, not a skip.
    /// </summary>
    [Fact]
    public async Task Proposal_RewriteIsRefused_WhenNoVoteOfRecordLedgerIsWired()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        var rewrite = await world.RewriteProposalAsync(
            proposal,
            author,
            new Dictionary<string, string>
            {
                [ConsumerGovernanceHolonTypes.ProposalMetadata.Options] =
                    ConsumerGovernanceHolonTypes.SerializeOptions(new[] { "maybe", "later" }),
            },
            useManagerWithNoVoteOfRecord: true);

        rewrite.IsError.Should().BeTrue();
        rewrite.Message.Should().Contain("vote-of-record ledger is unavailable");
    }

    // ── the electorate must name a tenant ───────────────────────────────────

    /// <summary>
    /// A plain user JWT carries no <c>act_as_tenant</c> claim, so an ordinary member
    /// raising a proposal used to record the electorate <c>node</c> — every tenant-less
    /// verified human on the node, in the same person-key domain node-global claims use.
    /// The proposal is refused instead, and nothing is left behind.
    /// </summary>
    [Fact]
    public async Task Proposal_RaisedWithNoActingTenant_IsRefused_AndPersistsNothing()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();

        var result = await world.TryCreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1), tenant: null);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain(ElectoratePrefix);
        world.Holons.All.Should().NotContain(
            h => h.AssetType == ConsumerGovernanceHolonTypes.ProposalAssetType,
            "a refused proposal leaves no half-built notice behind");
    }

    /// <summary>
    /// The node handler is only one of the two ways a proposal holon is written. This is
    /// the other: an ordinary <c>POST /api/holon</c> naming the type by hand.
    /// </summary>
    [Fact]
    public async Task Proposal_CannotBeHandWrittenWithTheNodeElectorate_ThroughTheHolonApi()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();

        var created = await world.Manager.CreateAsync(
            new HolonCreateModel
            {
                Name = "Hand-written notice",
                AssetType = ConsumerGovernanceHolonTypes.ProposalAssetType,
                IsPublic = true,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ConsumerGovernanceHolonTypes.ProposalMetadata.Status] =
                        ConsumerGovernanceHolonTypes.StatusOpen,
                    [ConsumerGovernanceHolonTypes.ProposalMetadata.Options] =
                        ConsumerGovernanceHolonTypes.SerializeOptions(new[] { "yes", "no" }),
                    [ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessScope] = "Tenant",
                    [ConsumerGovernanceHolonTypes.ProposalMetadata.ElectorateTenant] =
                        ConsumerGovernanceHolonTypes.NodeElectorate,
                },
            },
            author);

        created.IsError.Should().BeTrue();
        created.Message.Should().StartWith(ElectoratePrefix);
    }

    /// <summary>An update may not widen a tenant electorate to the node either.</summary>
    [Fact]
    public async Task Proposal_CannotBeMovedToTheNodeElectorate_ByAnUpdate()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            author, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        var rewrite = await world.RewriteProposalAsync(
            proposal,
            author,
            new Dictionary<string, string>
            {
                [ConsumerGovernanceHolonTypes.ProposalMetadata.ElectorateTenant] =
                    ConsumerGovernanceHolonTypes.NodeElectorate,
            });

        rewrite.IsError.Should().BeTrue();
        rewrite.Message.Should().StartWith(ElectoratePrefix);
    }

    /// <summary>
    /// A row written into storage BEFORE the electorate rule existed — the only way one
    /// can now exist — is not handed back as a live notice, so it can be neither voted on
    /// nor tallied. The refusal reuses the "no such proposal" sentence.
    /// </summary>
    [Fact]
    public async Task Proposal_RecordedWithTheNodeElectorate_IsNeitherReadableNorVotable()
    {
        var world = new GovernanceWorld();
        var author = world.VerifiedUser();
        var member = world.VerifiedUser();

        var legacy = new Holon
        {
            Id = Guid.NewGuid(),
            Name = "Written before the rule",
            UserId = author,
            AssetType = ConsumerGovernanceHolonTypes.ProposalAssetType,
            IsPublic = true,
        };
        legacy.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.Status] =
            ConsumerGovernanceHolonTypes.StatusOpen;
        legacy.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.Options] =
            ConsumerGovernanceHolonTypes.SerializeOptions(new[] { "yes", "no" });
        legacy.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessScope] = "Tenant";
        legacy.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey] =
            ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(legacy.Id);
        legacy.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.ElectorateTenant] =
            ConsumerGovernanceHolonTypes.NodeElectorate;

        // Straight into the store: the manager is exactly what refuses to write this.
        await world.Holons.UpsertAsync(legacy);

        (await world.Manager.GetAsync(legacy.Id, member)).IsError.Should().BeTrue();
        (await world.Manager.GetAsync(legacy.Id, author)).IsError.Should()
            .BeTrue("its own author cannot vote it into being a real notice either");

        var vote = await world.VoteAsync(member, legacy.Id, "yes");
        vote.IsError.Should().BeTrue();
        vote.Message.Should().Be(ProposalUnreadable);
        world.LiveClaimsOn(legacy.Id).Should().BeEmpty();
    }
}
