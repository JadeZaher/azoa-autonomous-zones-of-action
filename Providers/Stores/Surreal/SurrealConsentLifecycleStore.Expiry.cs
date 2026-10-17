using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Grant = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;
using Transition = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentLifecycleTransition;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealConsentLifecycleStore
{
    public Task<IReadOnlyList<Grant>> ListUnnotifiedExpiryAsync(int limit, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return executor.QueryAsync<Grant>(SurrealQuery<Grant>.From()
            .Where(row => row.RevokedAt == null && row.ExpiryNotifiedAt == null && row.ExpiresAt != null && row.ExpiresAt <= now)
            .OrderBy(row => row.ExpiresAt!).ThenBy(row => row.Id).Limit(Math.Clamp(limit, 1, 100)), ct);
    }

    public async Task<bool> RecordExpiryAsync(Guid grantId, CancellationToken ct = default)
    {
        if (grantId == Guid.Empty) return false;
        var id = grantId.ToString("N");
        var grant = await executor.QuerySingleAsync<Grant>(SurrealQuery<Grant>.Key(id), ct);
        if (grant is null || grant.RevokedAt is not null || grant.ExpiresAt is not { } expiry
            || expiry > DateTimeOffset.UtcNow || grant.Version is < 0 or long.MaxValue) return false;
        var original = Snapshot(grant);
        if (grant.ExpiryNotifiedAt is not null)
        {
            var frozen = ConsentLifecycleIdentity.Transition(original, ConsentWebhookEventType.Expired, expiry);
            var recorded = await executor.QuerySingleAsync<Transition>(SurrealQuery<Transition>.Key(frozen.Id), ct);
            return recorded is not null && recorded.Kind == "Expired" && recorded.GrantId == id
                && recorded.OccurredAt == expiry && recorded.BodyHash == frozen.BodyHash;
        }
        var notification = await ConsentNotificationPlan.PrepareAsync(executor, original, ConsentWebhookEventType.Expired,
            expiry, "observed persisted expiry", ct);
        var proof = notification.Evidence(original, "{\"kind\":\"PersistedExpiry\"}");
        // raw: database-time expiry, exact unrevoked grant, audit and notification are one transition.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN"),
            SurrealQuery.Of("IF $_consent_expiry > time::now() { THROW 'CONSENT_LIFECYCLE_CHANGED' }").WithParam("_consent_expiry", expiry) };
        statements.AddRange(notification.DestinationFence);
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Grant>(id)
            .Where(row => row.Version == grant.Version && row.RevokedAt == null && row.ExpiryNotifiedAt == null
                && row.GrantorUserId == grant.GrantorUserId && row.TenantId == grant.TenantId && row.Scopes == grant.Scopes
                && row.Origin == grant.Origin && row.ParticipationRef == grant.ParticipationRef
                && row.GrantedAt == grant.GrantedAt && row.ExpiresAt == expiry)
            .Set(row => row.ExpiryNotifiedAt, DateTimeOffset.UtcNow).Set(row => row.Version, grant.Version + 1).Build(),
            "consent_expired", "_consent_expired"));
        statements.Add(SurrealQuery.Of("IF $_consent_expired = NONE OR $_consent_expired = [] { THROW 'CONSENT_LIFECYCLE_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Audit), "consent_expiry_audit"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Event), "consent_expiry_event"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(proof), "consent_expiry_transition"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct); response.EnsureAllOk(); return response;
            }, ct);
            return true;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal))
        {
            var winner = await executor.QuerySingleAsync<Transition>(SurrealQuery<Transition>.Key(proof.Id), ct);
            return winner is not null && winner.Kind == proof.Kind && winner.GrantId == id
                && winner.OccurredAt == expiry && winner.BodyHash == proof.BodyHash;
        }
    }
}
