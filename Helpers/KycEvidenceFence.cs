using AZOA.WebAPI.Core.Surreal;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Helpers;

/// <summary>Serializes subject-scoped evidence changes with decisions that consume their latest evidence.</summary>
public static class KycEvidenceFence
{
    public static Task<SurrealResponse> ExecuteAsync(ISurrealExecutor executor, SurrealQuery mutation, CancellationToken ct)
        => SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await executor.ExecuteAsync(mutation, ct);
            response.EnsureAllOk();
            return response;
        }, ct);

    public static string GuardId(string userId, string? tenantId)
        => $"attempt-{SurrealLink.FromLink(userId)}-{(string.IsNullOrEmpty(tenantId) ? "direct" : SurrealLink.FromLink(tenantId))}";

    public static SurrealQuery Create(string? userId, string? tenantId) => Create([userId], tenantId);

    // raw: evidence append and its consumer share these write locks inside their enclosing transaction; see AGENTS.md.
    public static SurrealQuery Create(IEnumerable<string?> userIds, string? tenantId)
        => CreateScopes(userIds.Select(user => (user, tenantId)));

    // Exactly one statement, no loop variable; see Helpers/AGENTS.md §Identity evidence serialization.
    public static SurrealQuery CreateScopes(IEnumerable<(string? UserId, string? TenantId)> scopes)
        => SurrealQuery.Of("""
            UPSERT $_evidence_guards
                SET kind = 'attempt', authority_revision = math::max([authority_revision ?? 0, 0]) + 1,
                    touched_at = time::now() RETURN NONE
            """).WithParam("_evidence_guards", scopes.Where(scope => !string.IsNullOrWhiteSpace(scope.UserId))
                .Select(scope => GuardId(scope.UserId!, scope.TenantId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(guardId => SurrealRecordParam.Of("kyc_authority_guard", guardId)).ToArray());

    public static SurrealQuery Mutation(string? userId, string? tenantId, SurrealQuery mutation)
        => SurrealQuery.Combine(SurrealQuery.Of("BEGIN"), Create(userId, tenantId), mutation, SurrealQuery.Of("COMMIT"));

    public static SurrealQuery Mutation(IEnumerable<string> userIds, string? tenantId, SurrealQuery mutation)
        => SurrealQuery.Combine(SurrealQuery.Of("BEGIN"), Create(userIds, tenantId), mutation, SurrealQuery.Of("COMMIT"));
}
