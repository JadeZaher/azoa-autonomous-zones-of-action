using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Grant = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;
using Membership = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentParticipationBoundary;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealConsentLifecycleStore
{
    public async Task<ConsentOffboardReceipt?> OffboardAsync(Guid tenantId, string participationRef,
        SigningConsentWitness authority, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || authority.Credential.ActorUserId != tenantId || authority.Credential.TenantId is not null
            || string.IsNullOrWhiteSpace(participationRef) || participationRef.Trim().Length > 1024) return null;
        var reference = participationRef.Trim();
        var guards = SigningAuthorityFence.ForConsent(authority, [AzoaScopes.TenantProvision], "consent_offboard");
        if (guards is null) return null;
        var id = ConsentLifecycleIdentity.ParticipationId(tenantId, reference);
        var membership = await executor.QuerySingleAsync<Membership>(SurrealQuery<Membership>.Key(id), ct);
        if (membership is not null && (membership.TenantId != tenantId.ToString("N") || membership.ParticipationRef != reference)) return null;
        if (membership?.ClosedAt is not null) return Closure(membership);
        if (membership?.Version is < 0 or long.MaxValue) return null;
        var tenant = SurrealLink.ToLink("user", tenantId.ToString("N"))!;
        var grants = await executor.QueryAsync<Grant>(SurrealQuery<Grant>.From()
            .Where(row => row.TenantId == tenant && row.Origin == "Participation" && row.ParticipationRef == reference && row.RevokedAt == null)
            .OrderBy(row => row.Id), ct);
        if (grants.Any(row => row.Version is < 0 or long.MaxValue)) return null;
        var now = DateTimeOffset.UtcNow;
        var receipt = new ConsentOffboardReceipt(tenantId, reference, now, grants.Count);
        var receiptJson = JsonSerializer.Serialize(receipt);
        var authorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority));
        // raw: membership epoch closure, every matching grant revocation and each immutable notification commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        if (membership is null)
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(new Membership { Id = id,
                TenantId = tenantId.ToString("N"), ParticipationRef = reference, Version = 1, CreatedAt = now, ClosedAt = now,
                ClosureReceiptJson = receiptJson, ClosureAuthorityJson = authorityJson }), "consent_offboard_create"));
        else
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Membership>(id)
                .Where(row => row.Version == membership.Version && row.ClosedAt == null && row.TenantId == membership.TenantId
                    && row.ParticipationRef == reference)
                .Set(row => row.Version, membership.Version + 1).Set(row => row.ClosedAt, now)
                .Set(row => row.ClosureReceiptJson, receiptJson).Set(row => row.ClosureAuthorityJson, authorityJson).Build(),
                "consent_offboard_close", "_consent_offboard_closed"));
            statements.Add(SurrealQuery.Of("IF $_consent_offboard_closed = NONE OR $_consent_offboard_closed = [] { THROW 'CONSENT_LIFECYCLE_CHANGED' }"));
        }
        ConsentNotificationPlan? destination = null;
        for (var index = 0; index < grants.Count; index++)
        {
            var grant = grants[index];
            var original = Snapshot(grant);
            var notification = destination is null
                ? await ConsentNotificationPlan.PrepareAsync(executor, original, ConsentWebhookEventType.Revoked, now, "participation offboard", ct)
                : destination.ForTransition(original, ConsentWebhookEventType.Revoked, now, "participation offboard");
            if (destination is null) statements.AddRange(notification.DestinationFence);
            destination ??= notification;
            var grantId = SurrealRecordGuid.BareId(grant.Id);
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Grant>(grantId)
                .Where(row => row.Version == grant.Version && row.RevokedAt == null && row.GrantorUserId == grant.GrantorUserId
                    && row.TenantId == tenant && row.Origin == "Participation" && row.ParticipationRef == reference
                    && row.Scopes == grant.Scopes && row.GrantedAt == grant.GrantedAt && row.ExpiresAt == grant.ExpiresAt)
                .Set(row => row.Version, grant.Version + 1).Set(row => row.RevokedAt, now).Build(),
                "consent_offboard_grant_" + index, "_consent_offboard_grant"));
            statements.Add(SurrealQuery.Of("IF $_consent_offboard_grant = NONE OR $_consent_offboard_grant = [] { THROW 'CONSENT_LIFECYCLE_CHANGED' }"));
            var proof = notification.Evidence(original, authorityJson);
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Audit), "consent_offboard_audit_" + index));
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Event), "consent_offboard_event_" + index));
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(proof), "consent_offboard_proof_" + index));
        }
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct); response.EnsureAllOk(); return response;
            }, ct);
            return receipt;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            membership = await executor.QuerySingleAsync<Membership>(SurrealQuery<Membership>.Key(id), ct);
            return membership?.ClosedAt is not null ? Closure(membership) : null;
        }
    }

    private static ConsentOffboardReceipt? Closure(Membership membership)
    {
        if (membership.ClosureReceiptJson is null) return null;
        var receipt = JsonSerializer.Deserialize<ConsentOffboardReceipt>(membership.ClosureReceiptJson);
        return receipt is not null && receipt.TenantId.ToString("N") == membership.TenantId
            && receipt.ParticipationRef == membership.ParticipationRef && receipt.ClosedAt == membership.ClosedAt && receipt.RevokedCount >= 0
            ? receipt : null;
    }
}
