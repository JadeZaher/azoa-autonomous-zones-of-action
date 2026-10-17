using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Commits election authority, votes and command receipts atomically.</summary>
public sealed partial class SurrealGovernanceStore(ISurrealExecutor executor)
{
    private const string Rejected = "GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED";

    public Task<long?> PolicyVersionAsync(CancellationToken ct)
        => NodeGovernancePolicyFence.ReadVersionAsync(executor, ct);

    public Task<GovernanceProposal?> GetAsync(string id, CancellationToken ct)
        => executor.QuerySingleAsync<GovernanceProposal>(SurrealQuery<GovernanceProposal>.Key(id), ct);

    public Task<GovernanceCommandReceipt?> ReceiptAsync(string id, CancellationToken ct)
        => executor.QuerySingleAsync<GovernanceCommandReceipt>(SurrealQuery<GovernanceCommandReceipt>.Key(id), ct);

    public Task<AZOAResult<GovernanceCommandReceipt>> RejectAsync(GovernanceCommandReceipt receipt, CancellationToken ct)
    {
        if (!receipt.IsError || string.IsNullOrWhiteSpace(receipt.Error))
            throw new ArgumentException("A rejected command requires a terminal validation result.", nameof(receipt));
        return CommitAsync([SurrealQuery.Of("BEGIN"), Scope(SurrealWriter.Create(receipt), "rejected_receipt"), SurrealQuery.Of("COMMIT")], receipt, ct);
    }

    public async Task<string[]> MembersAsync(string tenant, CancellationToken ct)
    {
        var link = SurrealLink.ToLink("user", tenant)!;
        var now = DateTimeOffset.UtcNow;
        var children = await executor.QueryAsync<User>(SurrealQuery<User>.From()
            .Where(user => user.OwnerTenantId == link && user.IsActive), ct);
        var grants = await executor.QueryAsync<ConsentGrant>(SurrealQuery<ConsentGrant>.From()
            .Where(grant => grant.TenantId == link && grant.Origin == "Participation" && grant.ParticipationRef != null
                && grant.ParticipationRef != "" && grant.RevokedAt == null && (grant.ExpiresAt == null || grant.ExpiresAt > now)), ct);
        return children.Select(user => SurrealLink.FromLink(user.Id)!).Concat(grants.Select(grant => SurrealLink.FromLink(grant.GrantorUserId)!))
            .Append(tenant).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public async Task<AZOAResult<GovernanceCommandReceipt>> CreateAsync(GovernanceProposal proposal, Holon projection,
        GovernanceCommandReceipt receipt, CancellationToken ct, long? expectedPolicyVersion = null)
    {
        var members = JsonSerializer.Deserialize<string[]>(proposal.ElectorateJson)!;
        // raw: membership snapshot, tenant authority, proposal projection and receipt are one atomic admission.
        List<SurrealQuery> queries = [SurrealQuery.Of("BEGIN"), ..Admin(proposal.TenantId),
            SurrealQuery.Of("IF $_closes <= time::now() { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }")
                .WithParam("_closes", proposal.ClosesAt),
            SurrealQuery.Of("""
                LET $_members = array::sort(array::distinct(array::concat(
                [$_tenant],
                (SELECT VALUE <string>record::id(id) FROM user WHERE is_active = true AND owner_tenant_id = type::record('user', $_tenant)),
                (SELECT VALUE <string>record::id(grantor_user_id) FROM consent_grant
                WHERE tenant_id = type::record('user', $_tenant) AND origin = 'Participation'
                AND participation_ref != NONE AND participation_ref != '' AND revoked_at = NONE
                AND (expires_at = NONE OR expires_at > time::now())))))
                """),
            SurrealQuery.Of("""
                IF $_members != $_expected_members { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }
                """).WithParam("_tenant", proposal.TenantId).WithParam("_expected_members", members),
            SurrealQuery.Of("""
                LET $_parent = IF $_has_parent THEN (UPDATE ONLY type::record('holon', $_parent_id) SET modified_date = modified_date
                    WHERE user_id = type::record('user', $_tenant) RETURN AFTER) ELSE NONE END
                """),
            SurrealQuery.Of("""
                IF $_has_parent AND $_parent = NONE { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }
                """).WithParam("_has_parent", projection.ParentHolonId is not null)
                    .WithParam("_parent_id", SurrealLink.FromLink(projection.ParentHolonId) ?? string.Empty).WithParam("_tenant", proposal.TenantId),
            Scope(SurrealWriter.Create(proposal), "proposal"), Scope(SurrealWriter.Create(projection), "projection"),
            Scope(SurrealWriter.Create(receipt), "receipt"), SurrealQuery.Of("COMMIT")];
        var policyFence = NodeGovernancePolicyFence.Create(expectedPolicyVersion);
        queries.InsertRange(1, policyFence);
        queries.Insert(1 + policyFence.Count, LocalEntityMutationFence.Holons());
        return await CommitAsync(queries, receipt, ct);
    }

    private async Task<AZOAResult<GovernanceCommandReceipt>> CommitAsync(IReadOnlyList<SurrealQuery> queries,
        GovernanceCommandReceipt expected, CancellationToken ct)
    {
        try
        {
            var fenced = queries.Take(1).Concat(ActionExecutionFence.Create(expected.RunId, expected.NodeId, expected.ActorId, expected.TenantId == "direct" ? null : expected.TenantId, expected.IntentHash)).Concat(queries.Skip(1)).ToArray();
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var result = await executor.ExecuteAsync(SurrealQuery.Combine(fenced), ct);
                result.EnsureAllOk();
                return result;
            }, ct);
        }
        catch (SurrealStatementException exception) when ((exception.Message.Contains(Rejected, StringComparison.Ordinal) || exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(NodeGovernancePolicyFence.FailureMarker, StringComparison.Ordinal))
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var existing = await ReceiptAsync(expected.Id, ct);
            return existing is not null && SameCommand(existing, expected)
                ? AZOAResult<GovernanceCommandReceipt>.Success(existing)
                : AZOAResult<GovernanceCommandReceipt>.Failure("Election authority or state changed; refresh and retry.");
        }
        var saved = await ReceiptAsync(expected.Id, ct) ?? throw new InvalidOperationException("Committed election receipt disappeared.");
        return SameCommand(saved, expected) ? AZOAResult<GovernanceCommandReceipt>.Success(saved)
            : AZOAResult<GovernanceCommandReceipt>.Failure("Command identity conflicts with an existing receipt.");
    }

    public static bool SameCommand(GovernanceCommandReceipt first, GovernanceCommandReceipt second)
        => first.Id == second.Id && first.ActorId == second.ActorId && first.TenantId == second.TenantId
            && first.RunId == second.RunId && first.NodeId == second.NodeId
            && first.IntentHash == second.IntentHash && first.ProposalId == second.ProposalId;

    private static SurrealQuery Scope(SurrealQuery statement, string scope) => SurrealGeneratedStatementScope.Apply(statement, scope);

    // raw: current tenant and provisioning credential participate in the election mutation transaction.
    private static IReadOnlyList<SurrealQuery> Admin(string tenant) => [SurrealQuery.Of("""
                LET $_admin = (UPDATE ONLY type::record('user', $_tenant) SET is_active = is_active WHERE is_active = true RETURN AFTER)
                """),
            SurrealQuery.Of("""
                LET $_keys = (UPDATE api_key SET is_active = is_active WHERE user_id = type::record('user', $_tenant) AND is_active = true
                            AND revoked_at = NONE AND (expires_at = NONE OR expires_at > time::now()) AND scopes CONTAINS $_provision RETURN AFTER)
                """),
            SurrealQuery.Of("""
                IF $_admin = NONE OR !$_admin.is_active OR array::len($_keys) = 0 { THROW 'GOVERNANCE_CONCURRENT_OR_UNAUTHORIZED' }
                """).WithParam("_tenant", tenant).WithParam("_provision", AzoaScopes.TenantProvision)];
}
