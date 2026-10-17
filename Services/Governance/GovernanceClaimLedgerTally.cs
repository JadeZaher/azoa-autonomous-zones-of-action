using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>Public aggregate counts without person identifiers.</summary>
public sealed record GovernanceVoteOfRecordCount(int TotalVotes, SortedDictionary<string, int> CountsByOption,
    string SourceTable, int LiveClaimsRead);

/// <summary>Validates cast-time claim evidence against an immutable election definition.</summary>
public static class GovernanceClaimLedgerTally
{
    /// <summary>Counts immutable, cast-time evidence against one authoritative election definition.</summary>
    public static AZOAResult<GovernanceVoteOfRecordCount> CountFrozenDecisions(GovernanceProposal proposal,
        IReadOnlyList<string> options, IReadOnlyList<PersonOperationClaim> rows)
    {
        var counts = new SortedDictionary<string, int>(options.ToDictionary(option => option, _ => 0), StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.ReleasedAt is not null) continue;
            if (string.IsNullOrWhiteSpace(row.UserId) || row.DecisionOption is null || !counts.ContainsKey(row.DecisionOption)
                || row.DecidedAt is null || row.DecidedAt < proposal.OpensAt || row.DecidedAt >= proposal.ClosesAt
                || row.DecisionClosesAt != proposal.ClosesAt || string.IsNullOrWhiteSpace(row.GovernanceEligibilityJson)
                || row.GovernanceDecisionHash != CanonicalJson.Hash(new { proposal.DefinitionHash, option = row.DecisionOption }))
                return AZOAResult<GovernanceVoteOfRecordCount>.Failure("The vote ledger contains inconsistent evidence; operator investigation is required.");
            counts[row.DecisionOption]++;
        }
        return AZOAResult<GovernanceVoteOfRecordCount>.Success(new(counts.Values.Sum(), counts, PersonOperationClaim.SchemaNameConst, rows.Count));
    }

}
