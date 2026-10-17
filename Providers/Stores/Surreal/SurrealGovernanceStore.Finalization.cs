using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealGovernanceStore
{
    public Task<IReadOnlyList<PersonOperationClaim>> DecisionsAsync(GovernanceProposal proposal, CancellationToken ct)
        => executor.QueryAsync<PersonOperationClaim>(SurrealQuery<PersonOperationClaim>.From()
            .Where(claim => claim.GovernanceProposalId == proposal.Id && claim.GovernanceDefinitionHash == proposal.DefinitionHash
                && claim.UniquenessScope == proposal.UniquenessScope && claim.OperationKey == proposal.OperationKey
                && claim.ReleasedAt == null && claim.DecisionClosesAt == proposal.ClosesAt)
            .Select(claim => new { claim.UserId, claim.ReleasedAt, claim.DecisionOption, claim.DecidedAt,
                claim.DecisionClosesAt, claim.GovernanceDecisionHash, claim.GovernanceEligibilityJson }), ct);

    public async Task<AZOAResult<GovernanceCommandReceipt>> FinalizeAsync(GovernanceProposal proposal,
        IReadOnlyDictionary<string, int> counts, GovernanceCommandReceipt receipt, CancellationToken ct)
    {
        // raw: finalization locks the proposal, verifies ledger counts after database cutoff, and commits immutable output/projection/receipt.
        SurrealQuery[] guard = [SurrealQuery.Of("""
                LET $_finalizing = (UPDATE ONLY type::record('governance_proposal', $_proposal_id) SET version += 1
                WHERE definition_hash = $_definition AND final_result_json = NONE AND closes_at <= time::now() RETURN AFTER)
                """),
            SurrealQuery.Of("""
                IF $_finalizing = NONE { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }
                """),
            SurrealQuery.Of("""
                LET $_decisions = (SELECT decision_option, governance_decision_hash, governance_eligibility_json, decided_at
                FROM person_operation_claim WHERE governance_proposal_id = $_proposal_id AND governance_definition_hash = $_definition
                AND uniqueness_scope = $_scope AND operation_key = $_operation AND released_at = NONE
                AND user_id != NONE AND user_id != '' AND decision_closes_at = $_close
                AND decided_at >= $_open AND decided_at < $_close AND governance_eligibility_json != NONE)
                """),
            SurrealQuery.Of("""
                IF array::len($_decisions) != $_total { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }
                """).WithParam("_proposal_id", proposal.Id).WithParam("_definition", proposal.DefinitionHash)
            .WithParam("_scope", proposal.UniquenessScope).WithParam("_operation", proposal.OperationKey)
            .WithParam("_close", proposal.ClosesAt).WithParam("_open", proposal.OpensAt).WithParam("_total", counts.Values.Sum())];
        List<SurrealQuery> queries = [SurrealQuery.Of("BEGIN"), LocalEntityMutationFence.Holons(), ..Admin(proposal.TenantId), ..guard];
        var index = 0;
        foreach (var pair in counts)
        {
            var suffix = (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var token = SurrealIdentifierTemplate.Token(suffix);
            queries.Add(SurrealIdentifierTemplate.Of($"IF array::len((SELECT VALUE decision_option FROM $_decisions WHERE decision_option = $_option{token})) != $_count{token} {{ THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }}")

                .WithParam("_option" + suffix, pair.Key).WithParam("_count" + suffix, pair.Value));
        }
        queries.Add(Scope(SurrealWriter.UpdateOnly<GovernanceProposal>(proposal.Id)
            .Set(row => row.FinalResultJson, receipt.OutputJson).Set(row => row.FinalizedAt, receipt.CreatedAt).Build(), "final"));
        queries.Add(SurrealQuery.Of("UPDATE ONLY type::record('holon', $_proposal_id) SET metadata.status = 'Closed' RETURN NONE")
            .WithParam("_proposal_id", proposal.Id));
        queries.Add(Scope(SurrealWriter.Create(receipt), "receipt"));
        queries.Add(SurrealQuery.Of("COMMIT"));
        return await CommitAsync(queries, receipt, ct);
    }
}
