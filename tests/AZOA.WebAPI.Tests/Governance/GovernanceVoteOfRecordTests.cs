// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Governance;

/// <summary>
/// Phase 0 of <c>governance-vote-of-record_20260906</c>: the attack tests, written
/// BEFORE the fix and expected to FAIL against the shipped tally.
/// </summary>
/// <remarks>
/// <para>
/// Every defect this track corrects is one instance of a single class — a control
/// that exists and is invoked on one path while the consumer of its result reads a
/// different, unprotected one. <c>IPersonUniquenessGate</c> is sound and
/// <c>GovernanceVoteNodeHandler</c> claims through it correctly; the tally then
/// derives the OUTCOME from child holons, which <c>POST /api/holon</c> writes and
/// <c>PUT /api/holon/{id}</c> rewrites. Joining the holons to the claim ledger (the
/// shipped <c>GovernanceBallotRoll</c>) narrows the hole without closing it: the
/// join still lets attacker-written holons decide WHICH option a claim is spent on.
/// </para>
/// <para>
/// D39 closes it by construction — the claim row IS the vote of record and the tally
/// reads nothing else — so each test below asserts an OUTCOME (the published counts),
/// never a mechanism. That is deliberate: these must survive the rewrite that makes
/// them pass, and must fail again the day someone reintroduces a holon read.
/// See <c>tests/AZOA.WebAPI.Tests/Governance/AGENTS.md</c>.
/// </para>
/// </remarks>
public class GovernanceVoteOfRecordTests
{
    /// <summary>Refusal a caller sees for a proposal they may not read, or one that never existed.</summary>
    private const string ProposalUnreadable = "governance proposal not found.";

    /// <summary>
    /// AC1 — THE 500-HOLON ATTACK. A verified user casts one sanctioned ballot and
    /// then writes five hundred more <c>GovernanceVote</c> children straight through
    /// the holon API, plus five hundred from a second verified user who never takes a
    /// claim at all. The published tally must be exactly the one ballot the vote node
    /// recorded.
    /// </summary>
    /// <remarks>
    /// PINS: a vote holon created outside <c>GovernanceVoteNodeHandler</c> must count
    /// for nothing. FAILS TODAY. <c>GovernanceBallotRoll</c> orders candidate ballots
    /// by <c>CastAtOrMax</c> — a string the forger writes — and spends the claim on
    /// whichever sorts first. The backdated forgeries therefore capture the claim
    /// holder's single vote and the tally publishes the FORGED option: today
    /// <c>no = 1, yes = 0</c>. Under D39 no holon is read, so the count is the claim
    /// row's option and the thousand forgeries are inert.
    /// </remarks>
    [Fact]
    public async Task Tally_CountsNoBallotHolonWrittenOutsideTheVoteNode()
    {
        var world = new GovernanceWorld();
        var owner = world.VerifiedUser();
        var member = world.VerifiedUser();
        var outsider = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            owner, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        // The one sanctioned ballot: through the node, consulting the gate, taking the claim.
        var cast = await world.VoteAsync(member, proposal, "yes");
        cast.IsError.Should().BeFalse(cast.Message ?? string.Empty);

        // 500 forgeries by the claim HOLDER, backdated so a timestamp-ordered tally
        // reaches them before the ballot the server actually recorded.
        for (var i = 0; i < 500; i++)
            await world.ForgeBallotHolonAsync(
                member, proposal, "no", GovernanceWorld.Start.AddYears(-1).AddSeconds(i));

        // 500 more by a verified user who never voted and holds no claim whatsoever.
        for (var i = 0; i < 500; i++)
            await world.ForgeBallotHolonAsync(
                outsider, proposal, "no", GovernanceWorld.Start.AddYears(-1).AddSeconds(i));

        // Nothing is asserted about how many forgeries the holon API accepted: refusing
        // them outright is one valid fix, and counting them is the defect. What must
        // hold either way is that exactly one human ever passed the gate.
        world.LiveClaimsOn(proposal).Should().HaveCount(1, "only one human passed the gate");

        var tally = GovernanceWorld.ReadTally(await world.TallyAsync(owner, proposal));

        tally.TotalVotes.Should().Be(1);
        tally.Counts["yes"].Should().Be(1);
        tally.Counts["no"].Should().Be(0);
    }

    /// <summary>
    /// AC3 (ordering half) — the tally's answer must not move when two ballots'
    /// client-supplied <c>castAt</c> strings are exchanged. Nothing about who decided
    /// what changes; only two attacker-writable fields swap places.
    /// </summary>
    /// <remarks>
    /// PINS: ordering must derive from SERVER-set values only. FAILS TODAY. The
    /// shipped ordering key is <c>GovernanceBallotRoll.CastAtOrMax</c>, read from
    /// holon metadata the ballot's owner controls, so exchanging the two strings
    /// changes which ballot spends the claim and flips the published counts and the
    /// tally hash. Stated as a metamorphic property rather than a fixed expectation so
    /// it prescribes no ordering rule: under D39 the tally reads no holon metadata at
    /// all, so the swap is invisible to it.
    /// </remarks>
    [Fact]
    public async Task Tally_IsUnchanged_WhenClientSuppliedTimestampsAreExchanged()
    {
        var world = new GovernanceWorld();
        var owner = world.VerifiedUser();
        var member = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            owner, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        var cast = await world.VoteAsync(member, proposal, "yes");
        cast.IsError.Should().BeFalse(cast.Message ?? string.Empty);

        // One forged sibling, stamped LATER than the real ballot so it loses the
        // ordering as things stand.
        await world.ForgeBallotHolonAsync(member, proposal, "no", GovernanceWorld.Start.AddMinutes(10));

        var before = GovernanceWorld.ReadTally(await world.TallyAsync(owner, proposal));

        world.ReverseBallotCastAtOrder(proposal);

        var after = GovernanceWorld.ReadTally(await world.TallyAsync(owner, proposal));

        after.Counts.Should().Equal(before.Counts);
        after.TotalVotes.Should().Be(before.TotalVotes);
        after.TallyHash.Should().Be(before.TallyHash);

        // And the surviving decision is the one the vote node recorded, both times.
        before.Counts["yes"].Should().Be(1);
        after.Counts["yes"].Should().Be(1);
    }

    /// <summary>
    /// AC4 — after <c>closesAt</c> the decision is frozen: a late ballot is refused,
    /// and the decision recorded before the close cannot be edited afterwards.
    /// </summary>
    /// <remarks>
    /// PINS both AC4 clauses. FAILS TODAY on the second. The refusal of a late ballot
    /// already holds (<c>GovernanceVoteNodeHandler.CheckVotingWindow</c>), but the
    /// recorded CHOICE lives in holon metadata and <c>HolonManager.UpdateAsync</c>
    /// blind-merges whatever metadata its owner sends — with no voting-window check
    /// anywhere on that path. So a voter rewrites <c>option</c> on their own cast
    /// ballot hours after the poll closed and the tally publishes the new answer.
    /// Under D39 the vote of record is the claim row, frozen at close.
    /// </remarks>
    [Fact]
    public async Task Tally_AfterClose_IsFrozen_AgainstLateBallotsAndBallotEdits()
    {
        var world = new GovernanceWorld();
        var owner = world.VerifiedUser();
        var member = world.VerifiedUser();
        var latecomer = world.VerifiedUser();
        var closesAt = GovernanceWorld.Start.AddHours(1);
        var proposal = await world.CreateProposalAsync(owner, new[] { "yes", "no" }, closesAt);

        var cast = await world.VoteAsync(member, proposal, "yes");
        cast.IsError.Should().BeFalse(cast.Message ?? string.Empty);

        world.Clock.Advance(TimeSpan.FromHours(2));

        // Clause one: no ballot is accepted after the close.
        var late = await world.VoteAsync(latecomer, proposal, "no");
        late.IsError.Should().BeTrue();
        world.LiveClaimsOn(proposal).Should().HaveCount(1, "a refused ballot takes no claim");

        // Clause two: the decision already recorded cannot be changed after the close.
        // Refusing the rewrite and ignoring it are both acceptable; publishing it is not.
        await world.RewriteBallotOptionsAsync(proposal, member, "no");

        var tally = GovernanceWorld.ReadTally(await world.TallyAsync(owner, proposal));

        tally.TotalVotes.Should().Be(1);
        tally.Counts["yes"].Should().Be(1);
        tally.Counts["no"].Should().Be(0);
    }

    /// <summary>
    /// D40 / AC3 (re-vote half) — voting again before the close REPLACES the earlier
    /// option. One claim row per person, updated in place; nothing is double-counted.
    /// </summary>
    /// <remarks>
    /// PINS: a member may change their mind while the poll is open, and the change
    /// creates no second vote of record. FAILS TODAY at the first assertion:
    /// <c>GovernanceVoteNodeHandler.FindRunnerBallotProblemAsync</c> refuses a second
    /// ballot outright ("this user has already voted on this proposal."), because with
    /// the outcome living in holons a re-vote would mean two ballots and an ordering
    /// rule to choose between them. D39 removes that cost: there is one row to update.
    /// </remarks>
    [Fact]
    public async Task Vote_CastAgainBeforeClose_ReplacesThePriorOption_WithNoDuplicateCounted()
    {
        var world = new GovernanceWorld();
        var owner = world.VerifiedUser();
        var member = world.VerifiedUser();
        var proposal = await world.CreateProposalAsync(
            owner, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        var first = await world.VoteAsync(member, proposal, "yes");
        first.IsError.Should().BeFalse(first.Message ?? string.Empty);

        world.Clock.Advance(TimeSpan.FromMinutes(5));

        var second = await world.VoteAsync(member, proposal, "no");
        second.IsError.Should().BeFalse(second.Message ?? string.Empty);

        var tally = GovernanceWorld.ReadTally(await world.TallyAsync(owner, proposal));

        tally.TotalVotes.Should().Be(1);
        tally.Counts["no"].Should().Be(1);
        tally.Counts["yes"].Should().Be(0);

        // One person, one row — a re-vote is an update, never an append.
        world.LiveClaimsOn(proposal).Should().HaveCount(1);
    }

    /// <summary>
    /// AC5 — a proposal is readable by a non-owner eligible voter, and NOT by everyone
    /// else on the node.
    /// </summary>
    /// <remarks>
    /// PINS both halves of AC5. FAILS TODAY on the second.
    /// <c>GovernanceProposalCreateNodeHandler</c> buys the first half with
    /// <c>IsPublic = true</c>, and <c>HolonManager.CanRead</c> is owner-or-public with
    /// no tenant term — so publication is NODE-WIDE, and a member of an unrelated
    /// tenant reads the proposal and is admitted to the vote path. AC5 asks for the
    /// electorate to be able to read it "without making holons broadly public"
    /// precisely because this repo has leaked cross-tenant through generic read
    /// surfaces three times. The outsider must get the SAME sentence as someone asking
    /// about a proposal id that does not exist, so the vote node is not an existence
    /// oracle either.
    /// </remarks>
    [Fact]
    public async Task Proposal_IsReadableByAnEligibleVoter_ButNotByTheWholeNode()
    {
        var world = new GovernanceWorld();
        var otherTenant = Guid.Parse("7e57a11a-0000-4000-8000-0000000000b2");

        var owner = world.VerifiedUser();
        var member = world.VerifiedUser();
        var outsider = world.VerifiedUser(otherTenant);

        var proposal = await world.CreateProposalAsync(
            owner, new[] { "yes", "no" }, closesAt: GovernanceWorld.Start.AddDays(1));

        // First half: an eligible voter who does not own the proposal can read it and vote.
        var eligible = await world.VoteAsync(member, proposal, "yes");
        eligible.IsError.Should().BeFalse(eligible.Message ?? string.Empty);

        // Second half: readability is the electorate's, not the whole node's.
        var stranger = await world.VoteAsync(outsider, proposal, "yes", tenant: otherTenant);
        var missing = await world.VoteAsync(outsider, Guid.NewGuid(), "yes", tenant: otherTenant);

        stranger.IsError.Should().BeTrue();
        stranger.Message.Should().Be(ProposalUnreadable);
        stranger.Message.Should().Be(missing.Message ?? string.Empty, "the vote node must not be a proposal-existence oracle");

        world.LiveClaimsOn(proposal).Should().HaveCount(1, "the outsider never entered this electorate");

        var tally = GovernanceWorld.ReadTally(await world.TallyAsync(owner, proposal));
        tally.TotalVotes.Should().Be(1);
        tally.Counts["yes"].Should().Be(1);
    }
}
