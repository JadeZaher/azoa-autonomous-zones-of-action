using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Governance;

public sealed class GovernanceVoteOfRecordTests
{
    private readonly GovernanceProposal _proposal = new() { DefinitionHash = "definition",
        OpensAt = DateTimeOffset.UtcNow.AddHours(-2), ClosesAt = DateTimeOffset.UtcNow.AddHours(-1) };

    private PersonOperationClaim Decision(string option) => new()
    {
        UserId = Guid.NewGuid().ToString("N"), DecisionOption = option, DecisionClosesAt = _proposal.ClosesAt,
        DecidedAt = _proposal.OpensAt.AddMinutes(1), GovernanceEligibilityJson = "{\"verifiedAtCast\":true}",
        GovernanceDecisionHash = CanonicalJson.Hash(new { _proposal.DefinitionHash, option })
    };

    [Fact]
    public void Tally_uses_cast_time_evidence_and_includes_zero_options_without_exposing_voters()
    {
        var first = Decision("yes"); var second = Decision("no");
        var result = GovernanceClaimLedgerTally.CountFrozenDecisions(_proposal, ["yes", "no", "abstain"], [first, second]);
        result.IsError.Should().BeFalse();
        result.Result!.TotalVotes.Should().Be(2);
        result.Result.CountsByOption["abstain"].Should().Be(0);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Result);
        json.Should().NotContain(first.UserId!).And.NotContain(second.UserId!).And.NotContain("personKey");
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("option")]
    [InlineData("cutoff")]
    [InlineData("late")]
    [InlineData("eligibility")]
    [InlineData("holder")]
    public void Corrupt_vote_evidence_refuses_finalization_instead_of_silently_dropping_a_vote(string corruption)
    {
        var row = Decision("yes");
        switch (corruption)
        {
            case "hash": row.GovernanceDecisionHash = "forged"; break;
            case "option": row.DecisionOption = "other"; break;
            case "cutoff": row.DecisionClosesAt = _proposal.ClosesAt.AddMinutes(1); break;
            case "late": row.DecidedAt = _proposal.ClosesAt; break;
            case "eligibility": row.GovernanceEligibilityJson = null; break;
            case "holder": row.UserId = null; break;
        }
        GovernanceClaimLedgerTally.CountFrozenDecisions(_proposal, ["yes", "no"], [row]).IsError.Should().BeTrue();
    }

    [Fact]
    public void Released_legacy_claims_do_not_count()
    {
        var released = Decision("yes"); released.ReleasedAt = _proposal.OpensAt;
        var result = GovernanceClaimLedgerTally.CountFrozenDecisions(_proposal, ["yes", "no"], [released]);
        result.Result!.TotalVotes.Should().Be(0);
    }
}
