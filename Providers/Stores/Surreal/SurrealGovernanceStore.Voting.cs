using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealGovernanceStore
{
    public async Task<AZOAResult<GovernanceCommandReceipt>> VoteAsync(GovernanceProposal proposal,
        PersonOperationClaim claim, KycSubmission approval, string identityId, GovernanceCommandReceipt receipt, CancellationToken ct)
    {
        // raw: election cutoff, live membership, exact KYC/AML lineage, person claim and receipt commit together.
        SurrealQuery[] guard = [SurrealQuery.Of("""
                LET $_proposal = (UPDATE ONLY type::record('governance_proposal', $_proposal_id) SET version += 1
                WHERE definition_hash = $_definition AND electorate_json = $_electorate AND final_result_json = NONE
                AND opens_at <= time::now() AND closes_at > time::now() RETURN AFTER)
                """),
            SurrealQuery.Of("""
                IF $_proposal = NONE { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }
                """),
            SurrealQuery.Of("""
                LET $_voter = (UPDATE ONLY type::record('user', $_voter_id) SET is_active = is_active WHERE is_active = true RETURN AFTER)
                """),
            SurrealQuery.Of("""
                LET $_participation = (UPDATE consent_grant SET revoked_at = revoked_at
                WHERE grantor_user_id = type::record('user', $_voter_id) AND tenant_id = type::record('user', $_tenant)
                AND origin = 'Participation' AND participation_ref != NONE AND participation_ref != ''
                AND revoked_at = NONE AND (expires_at = NONE OR expires_at > time::now()) RETURN AFTER)
                """),
            SurrealQuery.Of("""
                IF $_voter = NONE OR ($_voter_id != $_tenant AND $_voter.owner_tenant_id != type::record('user', $_tenant)
                AND array::len($_participation) = 0) { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }
                """),
            SurrealQuery.Of("""
                LET $_approval = (SELECT * FROM kyc_submission WHERE user_id = type::record('user', $_voter_id)
                AND tenant_id = type::record('user', $_tenant) ORDER BY submitted_at DESC LIMIT 1)
                """),
            SurrealQuery.Of("""
                IF array::len($_approval) != 1 OR $_approval[0].id != type::record('kyc_submission', $_approval_id)
                OR $_approval[0].status != 'APPROVED' OR $_approval[0].expires_at <= time::now()
                OR $_approval[0].provider_result != $_approval_envelope OR $_approval[0].aml_lineage_key != $_lineage
                OR $_approval[0].provider_selection_version != $_selection_version OR $_approval[0].provider_trust_revision != $_trust_revision
                { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }
                """),
            SurrealQuery.Of("""
                LET $_selection = (UPDATE ONLY type::record('tenant_kyc_provider_selection', $_tenant)
                SET selection_version = selection_version RETURN AFTER)
                """),
            SurrealQuery.Of("""
                LET $_profile = (UPDATE ONLY type::record('kyc_provider_profile', $_approval[0].provider_key)
                SET trust_revision = trust_revision RETURN AFTER)
                """),
            SurrealQuery.Of("""
                IF $_selection = NONE OR $_profile = NONE OR !$_profile.enabled
                OR $_selection.provider_key != $_approval[0].provider_key
                OR $_selection.selection_version != $_approval[0].provider_selection_version
                OR $_profile.trust_revision != $_approval[0].provider_trust_revision
                { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }
                """),
            SurrealQuery.Of("""
                LET $_identity = (SELECT * FROM ONLY type::record('kyc_identity', $_identity_id))
                """),
            SurrealQuery.Of("""
                IF $_identity = NONE OR $_identity.erased_at != NONE OR !($_identity.user_ids CONTAINS $_voter_id)
                OR $_identity.tenant_scope != $_tenant
                OR (IF $_global THEN $_identity.global_person_key ELSE $_identity.person_key END) != $_person
                { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }
                """),
            SurrealQuery.Of("""
                LET $_aml = (SELECT * FROM kyc_screening_result WHERE subject_user_id = type::record('user', $_voter_id)
                AND tenant_id = type::record('user', $_tenant) AND lineage_key = $_lineage ORDER BY screened_at DESC LIMIT 1)
                """),
            SurrealQuery.Of("""
                IF array::len($_aml) != 1 OR $_aml[0].decision != 'NO_HIT' OR $_aml[0].results_truncated
                { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }
                """),
            SurrealQuery.Of("""
                LET $_prior = (SELECT * FROM ONLY type::record('person_operation_claim', $_claim_id))
                """),
            SurrealQuery.Of("""
                IF $_prior != NONE AND ($_prior.user_id != $_voter_id OR $_prior.released_at != NONE
                OR $_prior.governance_proposal_id != $_proposal_id OR $_prior.governance_definition_hash != $_definition
                OR $_prior.decision_closes_at != $_proposal.closes_at)
                { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }
                """)
            .WithParam("_proposal_id", proposal.Id).WithParam("_definition", proposal.DefinitionHash).WithParam("_electorate", proposal.ElectorateJson)
            .WithParam("_voter_id", receipt.ActorId).WithParam("_tenant", proposal.TenantId)
            .WithParam("_approval_id", SurrealLink.FromLink(approval.Id)).WithParam("_approval_envelope", approval.ProviderResult!)
            .WithParam("_lineage", approval.AmlLineageKey!).WithParam("_claim_id", claim.Id)
            .WithParam("_identity_id", identityId).WithParam("_selection_version", approval.ProviderSelectionVersion)
            .WithParam("_trust_revision", approval.ProviderTrustRevision)
            .WithParam("_global", proposal.UniquenessScope != proposal.TenantId).WithParam("_person", claim.PersonKey)];
        var membership = System.Text.Json.JsonSerializer.Deserialize<string[]>(proposal.ElectorateJson)!;
        if (!membership.Contains(receipt.ActorId, StringComparer.Ordinal))
            return AZOAResult<GovernanceCommandReceipt>.Failure("Voter is not in the fixed electorate.");
        return await CommitAsync([SurrealQuery.Of("BEGIN"), KycEvidenceFence.Create(receipt.ActorId, proposal.TenantId), ..guard,
            Scope(SurrealWriter.Upsert(claim), "claim"),
            SurrealQuery.Of("""
                LET $_cast = (UPDATE ONLY type::record('person_operation_claim', $_claim_id)
                SET decided_at = time::now(), modified_date = time::now()
                WHERE time::now() >= $_open AND time::now() < $_close RETURN AFTER)
                """),
            SurrealQuery.Of("""
                IF $_cast = NONE { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }
                """).WithParam("_claim_id", claim.Id).WithParam("_open", proposal.OpensAt).WithParam("_close", proposal.ClosesAt),
            Scope(SurrealWriter.Create(receipt), "receipt"),
            SurrealQuery.Of("COMMIT")], receipt, ct);
    }
}
