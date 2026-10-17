using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Command = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentLifecycleCommandReceipt;
using Grant = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;
using User = AZOA.WebAPI.Persistence.SurrealDb.Models.User;
using Participation = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentParticipationBoundary;
using Approval = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentParticipationApproval;
using AZOA.WebAPI.Services.Consent;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Atomic consent lifecycle persistence; see the active consent outbox contract.</summary>
public sealed partial class SurrealConsentLifecycleStore(ISurrealExecutor executor)
{
    private const string Conflict = "CONSENT_LIFECYCLE_CHANGED";

    public Task<ConsentLifecycleReceipt?> AdmitUserExplicitAsync(ConsentLifecycleCommand input,
        SigningCredentialWitness credential, CancellationToken ct = default)
        => input.Origin == GrantOrigin.UserExplicit ? AdmitDirectAsync(input, credential, ct) : Task.FromResult<ConsentLifecycleReceipt?>(null);

    public Task<ConsentLifecycleReceipt?> AdmitParticipationAsync(ConsentLifecycleCommand input,
        SigningCredentialWitness credential, CancellationToken ct = default)
        => input.Origin == GrantOrigin.Participation ? AdmitDirectAsync(input, credential, ct) : Task.FromResult<ConsentLifecycleReceipt?>(null);

    public Task<ConsentLifecycleReceipt?> AdmitProvisionedAsync(Guid grantorId, string externalUserId,
        SigningConsentWitness authority, CancellationToken ct = default)
    {
        var tenantId = authority.Credential.ActorUserId;
        if (grantorId == Guid.Empty || tenantId == Guid.Empty || authority.Credential.TenantId is not null
            || string.IsNullOrWhiteSpace(externalUserId) || externalUserId.Trim().Length > 128 || externalUserId.Any(char.IsControl))
            return Task.FromResult<ConsentLifecycleReceipt?>(null);
        var guards = SigningAuthorityFence.ForConsent(authority, [AzoaScopes.TenantProvision], "consent_provision");
        if (guards is null) return Task.FromResult<ConsentLifecycleReceipt?>(null);
        var external = externalUserId.Trim();
        var requestId = TenantProvisionedGrant.RequestIdFor(tenantId, grantorId, external);
        var command = new ConsentLifecycleCommand(requestId, tenantId, TenantProvisionedGrant.ImpliedScopes.ToArray(),
            GrantOrigin.Participation, TenantProvisionedGrant.ParticipationRefFor(tenantId, external), null);
        return AdmitAsync(command, grantorId, authority.Credential, guards, external, ct);
    }

    private Task<ConsentLifecycleReceipt?> AdmitDirectAsync(ConsentLifecycleCommand input,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (credential.Kind != "Jwt" || credential.TokenUse != AzoaClaims.TokenUseLogin
            || credential.TenantId is not null || !credential.OwnerAuthority) return Task.FromResult<ConsentLifecycleReceipt?>(null);
        var guards = SigningAuthorityFence.ForRunAdmission(credential, "consent_admit");
        return guards is null ? Task.FromResult<ConsentLifecycleReceipt?>(null)
            : AdmitAsync(input, credential.ActorUserId, credential, guards, null, ct);
    }

    private async Task<ConsentLifecycleReceipt?> AdmitAsync(ConsentLifecycleCommand input, Guid grantorId,
        SigningCredentialWitness credential, IReadOnlyList<SurrealQuery> guards, string? provisionedExternalId, CancellationToken ct)
    {
        var command = ConsentLifecycleIdentity.Normalize(input);
        var id = ConsentLifecycleIdentity.CommandId(grantorId, command.RequestId);
        var hash = CanonicalJson.Hash(command);
        var prior = await executor.QuerySingleAsync<Command>(SurrealQuery<Command>.Key(id), ct);
        if (prior is not null) return Replay(prior, hash);
        if (command.Scopes.Any(scope => !AzoaScopes.IsIssuableByUser(scope))) return null;
        var now = DateTimeOffset.UtcNow;
        if (command.ExpiresAt <= now) return null;
        Participation? membership = null;
        Approval? approval = null;
        string? approvalId = null;
        User? provisionedUser = null;
        string? membershipId = null;
        if (provisionedExternalId is not null)
        {
            provisionedUser = await executor.QuerySingleAsync<User>(SurrealQuery<User>.Key(grantorId.ToString("N")), ct);
            if (provisionedUser is not { IsActive: true } || SurrealRecordGuid.ParseOptional(provisionedUser.OwnerTenantId) != command.TenantId
                || provisionedUser.ExternalUserId != provisionedExternalId) return null;
        }
        if (command.Origin == GrantOrigin.Participation)
        {
            membershipId = ConsentLifecycleIdentity.ParticipationId(command.TenantId, command.ParticipationRef!);
            membership = await executor.QuerySingleAsync<Participation>(SurrealQuery<Participation>.Key(membershipId), ct);
            if (membership is not null && (membership.ClosedAt is not null || membership.Version is < 0 or long.MaxValue
                || membership.TenantId != command.TenantId.ToString("N") || membership.ParticipationRef != command.ParticipationRef)) return null;
            approvalId = ConsentLifecycleIdentity.ApprovalId(membershipId, grantorId);
            approval = await executor.QuerySingleAsync<Approval>(SurrealQuery<Approval>.Key(approvalId), ct);
            if (approval is null ? provisionedUser is null : !approval.IsActive || approval.MembershipId != membershipId
                || approval.TenantId != command.TenantId.ToString("N") || approval.GrantorId != grantorId.ToString("N")
                || approval.SourceKind != "TenantProvisioned" || string.IsNullOrWhiteSpace(approval.SourceCommandId)) return null;
        }
        var grantId = Guid.ParseExact(id[..32], "N");
        var receipt = new ConsentLifecycleReceipt(command.RequestId, grantId, grantorId, command.TenantId,
            command.Scopes.ToArray(), command.Origin, command.ParticipationRef, now, command.ExpiresAt);
        var tenant = SurrealLink.ToLink("user", command.TenantId.ToString("N"))!;
        var actor = SurrealLink.ToLink("user", grantorId.ToString("N"))!;
        var receiptJson = JsonSerializer.Serialize(receipt);
        var grant = new Grant { Id = grantId.ToString("N"), GrantorUserId = actor, TenantId = tenant,
            Scopes = string.Join(',', command.Scopes), Origin = command.Origin.ToString(), GrantedAt = now,
            ExpiresAt = command.ExpiresAt, CommandId = id, AdmissionReceiptJson = receiptJson, ParticipationRef = command.ParticipationRef };
        var notification = await ConsentNotificationPlan.PrepareAsync(executor, receipt, ConsentWebhookEventType.Granted, now, command.Origin.ToString(), ct);
        var saved = new Command { Id = id, GrantorId = grantorId.ToString("N"), TenantId = command.TenantId.ToString("N"),
            GrantId = grant.Id, IntentHash = hash, AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
                { Credential = credential, ProvisionedExternalId = provisionedExternalId, SourceAuthNotBefore = provisionedUser?.AuthNotBefore })),
            ReceiptJson = receiptJson, CreatedAt = now };
        // raw: current authority, grant, audit, frozen notification and original command receipt commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        if (provisionedUser is not null)
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<User>(grantorId.ToString("N"))
                .Where(row => row.IsActive && row.OwnerTenantId == provisionedUser.OwnerTenantId
                    && row.ExternalUserId == provisionedExternalId && row.AuthNotBefore == provisionedUser.AuthNotBefore)
                .Set(row => row.IsActive, true).Build(), "consent_provisioned_user", "_consent_provisioned_user"));
            statements.Add(SurrealQuery.Of("IF $_consent_provisioned_user = NONE OR $_consent_provisioned_user = [] { THROW 'CONSENT_LIFECYCLE_CHANGED' }"));
        }
        if (command.ExpiresAt is { } expiry)
            statements.Add(SurrealQuery.Of("IF $_consent_expiry <= time::now() { THROW 'CONSENT_LIFECYCLE_CHANGED' }").WithParam("_consent_expiry", expiry));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<User>(command.TenantId.ToString("N"))
            .Where(row => row.IsActive).Set(row => row.IsActive, true).Build(), "consent_tenant", "_consent_tenant"));
        statements.Add(SurrealQuery.Of("IF $_consent_tenant = NONE OR $_consent_tenant = [] { THROW 'CONSENT_LIFECYCLE_CHANGED' }"));
        statements.AddRange(notification.DestinationFence);
        if (membershipId is not null)
        {
            if (approval is null)
                statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(new Approval { Id = approvalId!,
                    MembershipId = membershipId, TenantId = command.TenantId.ToString("N"), GrantorId = grantorId.ToString("N"),
                    SourceCommandId = id, SourceKind = "TenantProvisioned", IsActive = true, CreatedAt = now }), "consent_approval_create"));
            else
            {
                statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Approval>(approvalId!)
                    .Where(row => row.IsActive && row.MembershipId == membershipId && row.TenantId == approval.TenantId
                        && row.GrantorId == approval.GrantorId && row.SourceKind == approval.SourceKind && row.SourceCommandId == approval.SourceCommandId)
                    .Set(row => row.IsActive, true).Build(), "consent_approval", "_consent_approval"));
                statements.Add(SurrealQuery.Of("IF $_consent_approval = NONE OR $_consent_approval = [] { THROW 'CONSENT_LIFECYCLE_CHANGED' }"));
            }
            if (membership is null)
                statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(new Participation { Id = membershipId,
                    TenantId = command.TenantId.ToString("N"), ParticipationRef = command.ParticipationRef!, Version = 1, CreatedAt = now }), "consent_membership_create"));
            else
            {
                statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Participation>(membershipId)
                    .Where(row => row.Version == membership.Version && row.ClosedAt == null
                        && row.TenantId == membership.TenantId && row.ParticipationRef == membership.ParticipationRef)
                    .Set(row => row.Version, membership.Version + 1).Build(), "consent_membership", "_consent_membership"));
                statements.Add(SurrealQuery.Of("IF $_consent_membership = NONE OR $_consent_membership = [] { THROW 'CONSENT_LIFECYCLE_CHANGED' }"));
            }
        }
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(grant), "consent_grant"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Audit), "consent_audit"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Event), "consent_event"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(saved), "consent_command"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(notification.Evidence(receipt, saved.AuthorityJson)), "consent_granted_proof"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk(); return response;
            }, ct);
            return receipt;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            prior = await executor.QuerySingleAsync<Command>(SurrealQuery<Command>.Key(id), ct);
            return prior is null ? null : Replay(prior, hash);
        }
    }

    private static ConsentLifecycleReceipt? Replay(Command prior, string hash)
        => !prior.Withdrawn && prior.IntentHash == hash ? JsonSerializer.Deserialize<ConsentLifecycleReceipt>(prior.ReceiptJson) : null;
}
