using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Grant = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;
using User = AZOA.WebAPI.Persistence.SurrealDb.Models.User;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealConsentLifecycleStore
{
    private const string RelinquishDetail = "relinquished by tenant";

    /// <summary>Revokes every live grant, of any origin, from the tenant's correlated member to that tenant; see Services/Consent/AGENTS.md §relinquish.</summary>
    public async Task<AZOAResult<ConsentRelinquishReceipt>> RelinquishAsync(Guid tenantId, string externalUserId,
        SigningConsentWitness authority, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || authority.Credential.ActorUserId != tenantId || authority.Credential.TenantId is not null
            || string.IsNullOrWhiteSpace(externalUserId) || externalUserId.Trim().Length > 128 || externalUserId.Any(char.IsControl))
            return AZOAResult<ConsentRelinquishReceipt>.FailureWithCode("Current tenant authority and a bounded external user id are required.", AzoaErrorCodes.Forbidden);
        var guards = SigningAuthorityFence.ForConsent(authority, [AzoaScopes.TenantProvision], "consent_relinquish");
        if (guards is null)
            return AZOAResult<ConsentRelinquishReceipt>.FailureWithCode("Current tenant authority is required.", AzoaErrorCodes.Forbidden);
        var external = externalUserId.Trim();
        var memberId = TenantChildCorrelation.DeterministicUserId(tenantId, external);
        var member = await executor.QuerySingleAsync<User>(SurrealQuery<User>.Key(memberId.ToString("N")), ct);
        if (member is null || member.ExternalUserId != external
            || !TenantChildCorrelation.IsCorrelated(memberId, SurrealRecordGuid.ParseOptional(member.OwnerTenantId), member.ExternalUserId, tenantId))
            return AZOAResult<ConsentRelinquishReceipt>.FailureWithCode("No member for that external user id.", AzoaErrorCodes.NotFound);
        var grantor = SurrealLink.ToLink("user", memberId.ToString("N"))!;
        var tenant = SurrealLink.ToLink("user", tenantId.ToString("N"))!;
        var selection = SurrealQuery<Grant>.From()
            .Where(row => row.GrantorUserId == grantor && row.TenantId == tenant && row.RevokedAt == null).OrderBy(row => row.Id);
        var grants = await executor.QueryAsync<Grant>(selection, ct);
        if (grants.Any(row => row.Version is < 0 or long.MaxValue))
            return AZOAResult<ConsentRelinquishReceipt>.FailureWithCode("A consent grant version cannot advance.", AzoaErrorCodes.Conflict);
        var now = DateTimeOffset.UtcNow;
        var receipt = new ConsentRelinquishReceipt(tenantId, external, now, grants.Count,
            grants.Select(row => SurrealRecordGuid.Parse(row.Id)).ToArray());
        var authorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority));
        // raw: the exact live grant set, every revocation and each immutable notification commit together.
        var statements = new List<SurrealQuery>
        {
            SurrealQuery.Of("BEGIN"),
        };
        statements.AddRange(guards);
        statements.Add(SurrealGeneratedStatementScope.Assign(selection, "relinquish_grant_set", "_relinquish_grant_set"));
        statements.Add(SurrealQuery.Of("IF array::len($_relinquish_grant_set) != $_relinquish_grant_count { THROW 'CONSENT_LIFECYCLE_CHANGED' }")
            .WithParam("_relinquish_grant_count", grants.Count));
        ConsentNotificationPlan? destination = null;
        for (var index = 0; index < grants.Count; index++)
        {
            var grant = grants[index];
            var original = Snapshot(grant);
            var notification = destination is null
                ? await ConsentNotificationPlan.PrepareAsync(executor, original, ConsentWebhookEventType.Revoked, now, RelinquishDetail, ct, "consent_relinquish")
                : destination.ForTransition(original, ConsentWebhookEventType.Revoked, now, RelinquishDetail);
            if (destination is null) statements.AddRange(notification.DestinationFence);
            destination ??= notification;
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Grant>(SurrealRecordGuid.BareId(grant.Id))
                .Where(row => row.Version == grant.Version && row.RevokedAt == null && row.GrantorUserId == grantor
                    && row.TenantId == tenant && row.Scopes == grant.Scopes && row.Origin == grant.Origin
                    && row.ParticipationRef == grant.ParticipationRef && row.GrantedAt == grant.GrantedAt && row.ExpiresAt == grant.ExpiresAt)
                .Set(row => row.Version, grant.Version + 1).Set(row => row.RevokedAt, now).Build(),
                "consent_relinquish_grant_" + index, "_consent_relinquish_grant"));
            statements.Add(SurrealQuery.Of("IF $_consent_relinquish_grant = NONE OR $_consent_relinquish_grant = [] { THROW 'CONSENT_LIFECYCLE_CHANGED' }"));
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Audit), "consent_relinquish_audit_" + index));
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Event), "consent_relinquish_event_" + index));
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Evidence(original, authorityJson)), "consent_relinquish_proof_" + index));
        }
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct); response.EnsureAllOk(); return response;
            }, ct);
            return AZOAResult<ConsentRelinquishReceipt>.Success(receipt,
                grants.Count == 0 ? "No live grant from this member remained." : "Every live grant from this member was relinquished.");
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            return AZOAResult<ConsentRelinquishReceipt>.FailureWithCode("Current tenant authority changed.", AzoaErrorCodes.Forbidden);
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal))
        {
            return AZOAResult<ConsentRelinquishReceipt>.FailureWithCode(
                "The member's grants changed concurrently; retry the same request.", AzoaErrorCodes.Conflict);
        }
    }
}
