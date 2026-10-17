using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Webhooks;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Row = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistration;
using Audit = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistrationAudit;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealWebhookRegistrationStore
{
    private const string ManagementConflict = "WEBHOOK_REGISTRATION_CHANGED";

    /// <inheritdoc/>
    public async Task<AZOAResult<WebhookRegistrationMutationResult>> MutateAsync(Guid tenantId,
        WebhookRegistrationCommand command, SigningConsentWitness authority, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || command.RequestId == Guid.Empty || command.RegistrationId == Guid.Empty
            || command.ExpectedVersion < 0 || command.ExpectedVersion == long.MaxValue
            || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length is < 10 or > 1000)
            return AZOAResult<WebhookRegistrationMutationResult>.Failure("An exact request, registration/version and 10-1000 character reason are required.");
        var credential = authority.Credential;
        var isOperator = credential.TokenUse == AzoaClaims.TokenUseNodeOperator;
        if (credential.TenantId is not null || (!isOperator && credential.ActorUserId != tenantId))
            return AZOAResult<WebhookRegistrationMutationResult>.Failure("Only this tenant's own credential or the node operator can manage its registration.");
        var scopes = new[] { isOperator ? AzoaScopes.Operator : AzoaScopes.TenantProvision };
        var fences = SigningAuthorityFence.ForConsent(authority, scopes, "webhook_management");
        if (fences is null) return AZOAResult<WebhookRegistrationMutationResult>.Failure("Current registration-management authority is required.");
        var auditId = CanonicalJson.Hash(new { Domain = "webhook-registration-command-v1", credential.ActorUserId, tenantId, command.RequestId });
        var requestHash = CanonicalJson.Hash(command with { Reason = command.Reason.Trim() });
        var prior = await ReadManagementAuditAsync(auditId, ct);
        if (prior is not null) return ReplayManagement(prior, requestHash, credential.ActorUserId, tenantId);
        var current = await ReadAsync(tenantId, ct);
        var legacy = current is not null && IsLegacy(current);
        if (current is null ? command.ExpectedVersion != 0 || command.ReplaceLegacy : SurrealRecordGuid.Parse(current.Id) != command.RegistrationId
            || current.Version != command.ExpectedVersion || current.SecretRevision == long.MaxValue || current.DestinationRevision == long.MaxValue
            || legacy != command.ReplaceLegacy || (legacy && command.LegacyFingerprint != LegacyFingerprint(current)))
            return AZOAResult<WebhookRegistrationMutationResult>.Failure("Registration changed or needs explicit legacy replacement.");
        if (!legacy && command.LegacyFingerprint is not null)
            return AZOAResult<WebhookRegistrationMutationResult>.Failure("Legacy replacement evidence does not apply to this registration.");
        var disableOnly = current is not null && !command.IsActive && command.Url == current.Url;
        if (!disableOnly && !urls.IsAllowed(command.Url, out _))
            return AZOAResult<WebhookRegistrationMutationResult>.Failure("The webhook destination is not permitted.");
        var newSecret = current is null || legacy || command.RotateSecret ? WebhookSecretProtector.GenerateSecret() : null;
        var now = DateTimeOffset.UtcNow;
        var secretRevision = current is null || legacy ? 1 : current.SecretRevision + (newSecret is null ? 0 : 1);
        var row = new Row
        {
            Id = command.RegistrationId.ToString("N"), TenantId = SurrealLink.ToLink("user", tenantId.ToString("N"))!,
            Url = command.Url, IsActive = command.IsActive, Version = command.ExpectedVersion + 1,
            DestinationRevision = current is null || legacy ? 1 : current.DestinationRevision + (current.Url == command.Url ? 0 : 1),
            SecretRevision = secretRevision, CreatedAt = current?.CreatedAt ?? now,
            SecretRotatedAt = current is not null && newSecret is not null ? now : current?.SecretRotatedAt,
            EncryptedSecret = newSecret is null ? current!.EncryptedSecret : secrets.Protect(tenantId, command.RegistrationId, secretRevision, newSecret),
        };
        var receipt = new WebhookRegistrationReceipt(command.RequestId, command.RegistrationId, tenantId, row.Version,
            row.DestinationRevision, row.SecretRevision, row.Url, row.IsActive, now);
        var audit = new Audit { Id = auditId, ActorId = credential.ActorUserId.ToString("N"), TenantId = tenantId.ToString("N"),
            RegistrationId = row.Id, RequestHash = requestHash, AuthorityJson = JsonSerializer.Serialize(authority),
            ReceiptJson = JsonSerializer.Serialize(receipt), Reason = command.Reason.Trim(), CreatedAt = now };
        // raw: exact registration CAS, current management authority and immutable receipt commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(fences);
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<AZOA.WebAPI.Persistence.SurrealDb.Models.User>(tenantId.ToString("N"))
            .Where(user => user.IsActive).Set(user => user.IsActive, true).Build(), "webhook_tenant", "_webhook_tenant"));
        statements.Add(SurrealQuery.Of("IF $_webhook_tenant = NONE { THROW 'WEBHOOK_REGISTRATION_CHANGED' }"));
        if (isOperator)
            statements.Add(SurrealQuery.Of("IF $_webhook_auth_time < time::now() - 5m { THROW 'SIGNING_AUTHORITY_CHANGED' }")
                .WithParam("_webhook_auth_time", credential.IssuedAt!.Value));
        if (current is null) statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(row), "webhook_create"));
        else
        {
            if (legacy)
            {
                statements.Add(SurrealGeneratedStatementScope.AssignRecord(SurrealQuery<Row>.Key(row.Id), "webhook_legacy", "_webhook_legacy"));
                statements.Add(SurrealQuery.Of("""
                    IF $_webhook_legacy = NONE OR ($_webhook_legacy.version ?? 0) != $_legacy_version
                        OR $_webhook_legacy.tenant_id != $_legacy_tenant OR $_webhook_legacy.url != $_legacy_url
                        OR ($_webhook_legacy.secret ?? '') != $_legacy_secret
                        OR ($_webhook_legacy.encrypted_secret ?? '') != $_legacy_encrypted
                        OR ($_webhook_legacy.secret_revision ?? 0) != $_legacy_secret_revision
                        OR ($_webhook_legacy.destination_revision ?? 0) != $_legacy_destination_revision
                        OR $_webhook_legacy.is_active != $_legacy_active
                        OR $_webhook_legacy.created_at != $_legacy_created
                        OR ($_legacy_has_rotation AND $_webhook_legacy.secret_rotated_at != $_legacy_rotation)
                        OR (!$_legacy_has_rotation AND $_webhook_legacy.secret_rotated_at != NONE)
                        { THROW 'WEBHOOK_REGISTRATION_CHANGED' }
                    """).WithParam("_legacy_version", current.Version).WithParam("_legacy_tenant", SurrealRecordParam.Of("user", tenantId.ToString("N")))
                    .WithParam("_legacy_url", current.Url).WithParam("_legacy_secret", current.LegacySecret ?? "")
                    .WithParam("_legacy_encrypted", current.EncryptedSecret ?? "").WithParam("_legacy_secret_revision", current.SecretRevision)
                    .WithParam("_legacy_destination_revision", current.DestinationRevision).WithParam("_legacy_active", current.IsActive)
                    .WithParam("_legacy_created", current.CreatedAt).WithParam("_legacy_has_rotation", current.SecretRotatedAt.HasValue)
                    .WithParam("_legacy_rotation", current.SecretRotatedAt ?? DateTimeOffset.UnixEpoch));
            }
            var update = SurrealWriter.UpdateOnly<Row>(row.Id).Where(item => item.TenantId == row.TenantId);
            if (!legacy) update = update.Where(item => item.Version == command.ExpectedVersion && item.TenantId == row.TenantId);
            update = update
                .Set(item => item.Url, row.Url).Set(item => item.IsActive, row.IsActive).Set(item => item.Version, row.Version)
                .Set(item => item.DestinationRevision, row.DestinationRevision).Set(item => item.SecretRevision, row.SecretRevision)
                .Set(item => item.EncryptedSecret, row.EncryptedSecret).Unset(item => item.LegacySecret);
            update = row.SecretRotatedAt is null ? update.Unset(item => item.SecretRotatedAt) : update.Set(item => item.SecretRotatedAt, row.SecretRotatedAt);
            statements.Add(SurrealGeneratedStatementScope.Assign(update.Build(), "webhook_update", "_webhook_updated"));
            statements.Add(SurrealQuery.Of("IF $_webhook_updated = NONE { THROW 'WEBHOOK_REGISTRATION_CHANGED' }"));
        }
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(audit), "webhook_audit"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var atomic = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(atomic, ct);
                response.EnsureAllOk();
            }, ct);
            return AZOAResult<WebhookRegistrationMutationResult>.Success(new(receipt, newSecret, false));
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(ManagementConflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            prior = await ReadManagementAuditAsync(auditId, ct);
            return prior is null ? AZOAResult<WebhookRegistrationMutationResult>.Failure("Registration or management authority changed.")
                : ReplayManagement(prior, requestHash, credential.ActorUserId, tenantId);
        }
    }

    private static bool IsLegacy(Row row) => row.Version < 1 || row.SecretRevision < 1 || row.DestinationRevision < 1
        || string.IsNullOrEmpty(row.EncryptedSecret) || row.LegacySecret is not null;

    private string LegacyFingerprint(Row row) => secrets.LegacyFingerprint(CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(row)));

    private Task<Audit?> ReadManagementAuditAsync(string id, CancellationToken ct)
        => executor.QuerySingleAsync<Audit>(SurrealQuery<Audit>.Key(id), ct);

    private static AZOAResult<WebhookRegistrationMutationResult> ReplayManagement(Audit audit, string hash, Guid actor, Guid tenant)
        => audit.Disposition == "Applied" && audit.RequestHash == hash && audit.ActorId == actor.ToString("N") && audit.TenantId == tenant.ToString("N")
            ? AZOAResult<WebhookRegistrationMutationResult>.Success(new(JsonSerializer.Deserialize<WebhookRegistrationReceipt>(audit.ReceiptJson)
                ?? throw new InvalidOperationException("Webhook management receipt is unreadable."), null, true))
            : AZOAResult<WebhookRegistrationMutationResult>.Failure("Request key conflicts with the original registration command.");
}
