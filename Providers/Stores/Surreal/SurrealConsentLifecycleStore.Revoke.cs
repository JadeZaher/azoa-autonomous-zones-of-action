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
using Transition = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentLifecycleTransition;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealConsentLifecycleStore
{
    public async Task<ConsentRevocationReceipt?> RevokeUserExplicitAsync(Guid grantId,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (grantId == Guid.Empty || credential.Kind != "Jwt" || credential.TokenUse != AzoaClaims.TokenUseLogin
            || credential.TenantId is not null || !credential.OwnerAuthority) return null;
        var guards = SigningAuthorityFence.ForRunAdmission(credential, "consent_revoke");
        if (guards is null) return null;
        var id = grantId.ToString("N");
        var grant = await executor.QuerySingleAsync<Grant>(SurrealQuery<Grant>.Key(id), ct);
        if (grant is null || SurrealRecordGuid.Parse(grant.GrantorUserId) != credential.ActorUserId) return null;
        if (grant.RevokedAt is not null) return await RevocationFactAsync(grant, ct);
        if (grant.Version is < 0 or long.MaxValue) return null;
        var original = Snapshot(grant);
        var now = DateTimeOffset.UtcNow;
        var notification = await ConsentNotificationPlan.PrepareAsync(executor, original, ConsentWebhookEventType.Revoked,
            now, "revoked by user", ct);
        var transition = notification.Evidence(original,
            CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(credential)));
        // raw: exact original grant revocation, audit, immutable transition and frozen notification commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.AddRange(notification.DestinationFence);
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Grant>(id)
            .Where(row => row.Version == grant.Version && row.RevokedAt == null && row.GrantorUserId == grant.GrantorUserId
                && row.TenantId == grant.TenantId && row.Scopes == grant.Scopes && row.Origin == grant.Origin
                && row.ParticipationRef == grant.ParticipationRef && row.GrantedAt == grant.GrantedAt && row.ExpiresAt == grant.ExpiresAt)
            .Set(row => row.RevokedAt, now).Set(row => row.Version, grant.Version + 1).Build(), "consent_revoked", "_consent_revoked"));
        statements.Add(SurrealQuery.Of("IF $_consent_revoked = NONE OR $_consent_revoked = [] { THROW 'CONSENT_LIFECYCLE_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Audit), "consent_revoke_audit"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Event), "consent_revoke_event"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(transition), "consent_revoke_transition"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct); response.EnsureAllOk(); return response;
            }, ct);
            return new(grantId, now, true);
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            var winner = await executor.QuerySingleAsync<Grant>(SurrealQuery<Grant>.Key(id), ct);
            return winner is not null && winner.GrantorUserId == grant.GrantorUserId && winner.RevokedAt is not null
                ? await RevocationFactAsync(winner, ct) : null;
        }
    }

    private async Task<ConsentRevocationReceipt> RevocationFactAsync(Grant grant, CancellationToken ct)
    {
        var original = Snapshot(grant);
        var snapshot = ConsentLifecycleIdentity.Transition(original, ConsentWebhookEventType.Revoked, grant.RevokedAt!.Value);
        var proof = await executor.QuerySingleAsync<Transition>(SurrealQuery<Transition>.Key(snapshot.Id), ct);
        var recorded = proof is not null && proof.GrantId == original.GrantId.ToString("N") && proof.Kind == "Revoked"
            && proof.OccurredAt == grant.RevokedAt && proof.EventId == snapshot.Id && proof.BodyHash == snapshot.BodyHash;
        return new(original.GrantId, grant.RevokedAt.Value, recorded);
    }

    private static ConsentLifecycleReceipt Snapshot(Grant grant)
    {
        var snapshot = new ConsentLifecycleReceipt(Guid.Empty, SurrealRecordGuid.Parse(grant.Id), SurrealRecordGuid.Parse(grant.GrantorUserId),
            SurrealRecordGuid.Parse(grant.TenantId), grant.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Enum.Parse<GrantOrigin>(grant.Origin), grant.ParticipationRef, grant.GrantedAt, grant.ExpiresAt);
        if (grant.AdmissionReceiptJson is null) return snapshot;
        var saved = JsonSerializer.Deserialize<ConsentLifecycleReceipt>(grant.AdmissionReceiptJson)
            ?? throw new InvalidDataException("Consent admission receipt is malformed.");
        if (CanonicalJson.Hash(saved with { RequestId = Guid.Empty }) != CanonicalJson.Hash(snapshot))
            throw new InvalidDataException("Consent grant differs from its immutable admission receipt.");
        return saved;
    }
}
