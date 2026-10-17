using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Grant = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealConsentLifecycleStore
{
    internal async Task<IReadOnlyList<SurrealQuery>> ClaimRevocationsAsync(Guid userId, Guid previousTenant,
        DateTimeOffset authenticationCutoff, CancellationToken ct)
    {
        var actor = SurrealLink.ToLink("user", userId.ToString("N"))!;
        var selection = SurrealQuery<Grant>.From().Where(row => row.GrantorUserId == actor && row.RevokedAt == null).OrderBy(row => row.Id);
        var grants = await executor.QueryAsync<Grant>(selection, ct);
        if (grants.Any(row => row.Version is < 0 or long.MaxValue))
            throw new InvalidDataException("Consent grant version cannot advance during account claim.");
        var occurredAt = DateTimeOffset.UtcNow;
        var authority = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
            { Kind = "AccountOwnershipClaim", UserId = userId, PreviousTenant = previousTenant, AuthenticationCutoff = authenticationCutoff }));
        // raw: the exact unrevoked grant set is checked inside the owning user-authentication CAS transaction.
        var statements = new List<SurrealQuery>
        {
            SurrealGeneratedStatementScope.Assign(selection, "claim_grant_set", "_claim_grant_set"),
            SurrealQuery.Of("IF array::len($_claim_grant_set) != $_claim_grant_count { THROW 'CONSENT_CLAIM_CHANGED' }")
                .WithParam("_claim_grant_count", grants.Count),
        };
        var destinations = new Dictionary<Guid, ConsentNotificationPlan>();
        for (var index = 0; index < grants.Count; index++)
        {
            var grant = grants[index];
            var original = Snapshot(grant);
            var notification = destinations.TryGetValue(original.TenantId, out var destination)
                ? destination.ForTransition(original, ConsentWebhookEventType.Revoked, occurredAt, "account claimed by owner")
                : await ConsentNotificationPlan.PrepareAsync(executor, original, ConsentWebhookEventType.Revoked,
                    occurredAt, "account claimed by owner", ct, "claim_destination_" + index);
            if (destination is null)
            {
                destinations.Add(original.TenantId, notification);
                statements.AddRange(notification.DestinationFence);
            }
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Grant>(SurrealRecordGuid.BareId(grant.Id))
                .Where(row => row.Version == grant.Version && row.RevokedAt == null && row.GrantorUserId == actor
                    && row.TenantId == grant.TenantId && row.Scopes == grant.Scopes && row.Origin == grant.Origin
                    && row.ParticipationRef == grant.ParticipationRef && row.GrantedAt == grant.GrantedAt && row.ExpiresAt == grant.ExpiresAt)
                .Set(row => row.Version, grant.Version + 1).Set(row => row.RevokedAt, occurredAt).Build(),
                "claim_revoke_" + index, "_claim_revoked"));
            statements.Add(SurrealQuery.Of("IF $_claim_revoked = NONE OR $_claim_revoked = [] { THROW 'CONSENT_CLAIM_CHANGED' }"));
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Audit), "claim_audit_" + index));
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Event), "claim_event_" + index));
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Evidence(original, authority)), "claim_proof_" + index));
        }
        return statements;
    }
}
