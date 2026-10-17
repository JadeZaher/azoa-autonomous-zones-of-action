using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealKybVerificationStore(ISurrealExecutor executor) : IKybVerificationStore
{
    /// <inheritdoc/>
    public Task<KybVerification?> GetAsync(Guid tenantId, CancellationToken ct = default)
        => executor.QuerySingleAsync<KybVerification>(SurrealQuery<KybVerification>.Key(tenantId.ToString("N")), ct);

    /// <inheritdoc/>
    public async Task<KybVerification?> TryStartAsync(KybVerification? expected, KybVerification candidate, CancellationToken ct = default)
    {
        if (expected is null)
        {
            try
            {
                var created = await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct);
                created.EnsureAllOk();
                return created.GetValues<KybVerification>(0).Single();
            }
            catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
            {
                if (await GetAsync(Guid.Parse(candidate.Id), ct) is not null) return null;
                throw;
            }
        }

        var query = SurrealWriter.UpdateOnly<KybVerification>(candidate.Id)
            .Where(row => row.Version == expected.Version && row.AttemptId == expected.AttemptId)
            .Set(row => row.AttemptId, candidate.AttemptId)
            .Set(row => row.AdmissionKey, candidate.AdmissionKey)
            .Set(row => row.RequestKeyHash, candidate.RequestKeyHash)
            .Set(row => row.AuthorityRevision, candidate.AuthorityRevision)
            .Set(row => row.Status, candidate.Status)
            .Set(row => row.Version, candidate.Version)
            .Unset(row => row.HostedSessionId)
            .Unset(row => row.BusinessEvidenceJson)
            .Unset(row => row.ReviewedAt)
            .Set(row => row.CreatedAt, candidate.CreatedAt)
            .Set(row => row.ExpiresAt, candidate.ExpiresAt)
            .Build();
        var response = await executor.ExecuteAsync(query, ct);
        response.EnsureAllOk();
        return response.GetValues<KybVerification>(0).SingleOrDefault();
    }

    /// <inheritdoc/>
    public async Task<bool> AttachSessionAsync(Guid tenantId, string attemptId, Guid sessionId, CancellationToken ct = default)
    {
        var query = SurrealWriter.UpdateOnly<KybVerification>(tenantId.ToString("N"))
            .Where(row => row.AttemptId == attemptId)
            .Set(row => row.HostedSessionId, sessionId.ToString("N")).Build();
        var response = await executor.ExecuteAsync(query, ct);
        response.EnsureAllOk();
        return response.GetValues<KybVerification>(0).Count == 1;
    }

    /// <inheritdoc/>
    public async Task<KybVerification?> TryDecideAsync(KybVerification expected, HostedSession session, string status, string? evidence,
        DateTimeOffset reviewedAt, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        if (status is not ("APPROVED" or "REJECTED") || string.IsNullOrWhiteSpace(session.ResumeLeaseToken)) return null;
        var decision = SurrealWriter.UpdateOnly<KybVerification>(expected.Id.Split(':').Last())
            .Where(row => row.AttemptId == expected.AttemptId && row.AuthorityRevision == expected.AuthorityRevision)
            .Where(row => row.Version == expected.Version && row.Status == "PENDING" && row.ExpiresAt > reviewedAt)
            .Set(row => row.Status, status)
            .Set(row => row.ReviewedAt, (DateTimeOffset?)reviewedAt)
            .Set(row => row.ExpiresAt, expiresAt)
            .Set(row => row.Version, expected.Version + 1);
        decision = evidence is null ? decision.Unset(row => row.BusinessEvidenceJson)
            : decision.Set(row => row.BusinessEvidenceJson, evidence);
        var query = decision.Build();
        // raw: hosted lease fence and tenant business decision must commit atomically.
        var transaction = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"),
            HostedSessionLeaseFence.CreateGuard(session), HostedSessionLeaseFence.AssertCurrent(),
            query, SurrealQuery.Of("COMMIT"));
        try
        {
            var response = await executor.ExecuteAsync(transaction, ct);
            response.EnsureAllOk();
            return response.GetValues<KybVerification>(3).SingleOrDefault();
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(HostedSessionLeaseFence.FailureMarker, StringComparison.Ordinal))
        {
            return null;
        }
    }
}
