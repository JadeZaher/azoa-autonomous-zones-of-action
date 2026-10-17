using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Audit = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistrationAudit;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealWebhookRegistrationStore
{
    /// <inheritdoc/>
    public async Task<AZOAResult<WebhookRegistrationWithdrawal>> WithdrawAsync(Guid tenantId,
        WebhookRegistrationCommand command, SigningConsentWitness authority, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || command.RequestId == Guid.Empty || command.RegistrationId == Guid.Empty
            || command.ExpectedVersion < 0 || string.IsNullOrWhiteSpace(command.Reason))
            return AZOAResult<WebhookRegistrationWithdrawal>.Failure("The exact saved registration command is required.");
        var credential = authority.Credential;
        var isOperator = credential.TokenUse == AzoaClaims.TokenUseNodeOperator;
        if (credential.TenantId is not null || (!isOperator && credential.ActorUserId != tenantId))
            return AZOAResult<WebhookRegistrationWithdrawal>.Failure("Only the original tenant authority can withdraw this command.");
        var fences = SigningAuthorityFence.ForConsent(authority, [isOperator ? AzoaScopes.Operator : AzoaScopes.TenantProvision], "webhook_withdrawal");
        if (fences is null) return AZOAResult<WebhookRegistrationWithdrawal>.Failure("Current management authority is required.");
        var id = CanonicalJson.Hash(new { Domain = "webhook-registration-command-v1", credential.ActorUserId, tenantId, command.RequestId });
        var hash = CanonicalJson.Hash(command with { Reason = command.Reason.Trim() });
        var prior = await ReadManagementAuditAsync(id, ct);
        if (prior is not null) return WithdrawalResult(prior, hash, credential.ActorUserId, tenantId, command.RequestId);
        var audit = new Audit { Id = id, ActorId = credential.ActorUserId.ToString("N"), TenantId = tenantId.ToString("N"),
            RegistrationId = command.RegistrationId.ToString("N"), RequestHash = hash, AuthorityJson = JsonSerializer.Serialize(authority),
            Disposition = "Withdrawn", Reason = command.Reason.Trim(), CreatedAt = DateTimeOffset.UtcNow };
        // raw: current authority and the unique command disposition serialize withdrawal against registration mutation.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(fences);
        if (isOperator)
            statements.Add(SurrealQuery.Of("IF $_withdraw_auth_time < time::now() - 5m { THROW 'SIGNING_AUTHORITY_CHANGED' }")
                .WithParam("_withdraw_auth_time", credential.IssuedAt!.Value));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(audit), "webhook_withdrawal_receipt"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var atomic = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(atomic, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return AZOAResult<WebhookRegistrationWithdrawal>.Success(new(command.RequestId, false, null));
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            prior = await ReadManagementAuditAsync(id, ct);
            return prior is null ? AZOAResult<WebhookRegistrationWithdrawal>.Failure("Management authority changed.")
                : WithdrawalResult(prior, hash, credential.ActorUserId, tenantId, command.RequestId);
        }
    }

    private static AZOAResult<WebhookRegistrationWithdrawal> WithdrawalResult(Audit audit, string hash, Guid actor, Guid tenant, Guid request)
    {
        if (audit.RequestHash != hash || audit.ActorId != actor.ToString("N") || audit.TenantId != tenant.ToString("N")
            || audit.Disposition is not ("Applied" or "Withdrawn"))
            return AZOAResult<WebhookRegistrationWithdrawal>.Failure("The saved command differs from this request key's disposition.");
        var receipt = audit.Disposition == "Applied" ? JsonSerializer.Deserialize<WebhookRegistrationReceipt>(audit.ReceiptJson)
            ?? throw new InvalidOperationException("Webhook management receipt is unreadable.") : null;
        return AZOAResult<WebhookRegistrationWithdrawal>.Success(new(request, audit.Disposition == "Applied", receipt));
    }
}
