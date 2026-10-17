using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Services.Webhooks;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionWebhookEvent;
using Registration = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistration;
using Audit = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookEventRetryAudit;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealActionWebhookDeliveryStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<WebhookEventStatus>> ListAsync(Guid tenantId, int offset, int limit, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return [];
        var tenant = SurrealLink.ToLink("user", tenantId.ToString("N"))!;
        var rows = await executor.QueryAsync<Event>(SurrealQuery<Event>.From().Where(row => row.TenantId == tenant)
            .OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id).Limit(Math.Clamp(limit, 1, 100)).Start(Math.Max(0, offset)), ct);
        return rows.Select(Status).ToArray();
    }

    /// <inheritdoc/>
    public async Task<WebhookEventStatus?> GetAsync(Guid tenantId, Guid eventId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || eventId == Guid.Empty) return null;
        var row = await executor.QuerySingleAsync<Event>(SurrealQuery<Event>.Key(eventId.ToString("N")), ct);
        return row is null || SurrealRecordGuid.Parse(row.TenantId) != tenantId ? null : Status(row);
    }

    /// <inheritdoc/>
    public async Task<WebhookEventRetryReceipt?> RetryAsync(Guid tenantId, Guid eventId, WebhookEventRetryCommand command,
        SigningConsentWitness authority, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || eventId == Guid.Empty || command.RequestId == Guid.Empty
            || command.ExpectedVersion is < 0 or long.MaxValue || string.IsNullOrWhiteSpace(command.Reason)
            || command.Reason.Trim().Length is < 10 or > 1000) return null;
        var credential = authority.Credential;
        var isOperator = credential.TokenUse == AzoaClaims.TokenUseNodeOperator;
        if (credential.TenantId is not null || (!isOperator && credential.ActorUserId != tenantId)) return null;
        var guards = SigningAuthorityFence.ForConsent(authority,
            [isOperator ? AzoaScopes.Operator : AzoaScopes.TenantProvision], "webhook_retry");
        if (guards is null) return null;
        var auditId = CanonicalJson.Hash(new { Domain = "webhook-event-retry-v1", credential.ActorUserId, tenantId, eventId, command.RequestId });
        var requestHash = CanonicalJson.Hash(command with { Reason = command.Reason.Trim() });
        var prior = await executor.QuerySingleAsync<Audit>(SurrealQuery<Audit>.Key(auditId), ct);
        if (prior is not null) return Replay(prior, requestHash);
        var id = eventId.ToString("N");
        var row = await executor.QuerySingleAsync<Event>(SurrealQuery<Event>.Key(id), ct);
        var now = DateTimeOffset.UtcNow;
        var origin = row is null ? null : RetryOrigin(row, now);
        if (row is null || origin is null || SurrealRecordGuid.Parse(row.TenantId) != tenantId || row.Version != command.ExpectedVersion
            || row.RetryGeneration is < 0 or long.MaxValue || row.ReceiptId is null || row.RegistrationId is null) return null;
        var accepted = await receipts.GetAsync(row.ReceiptId, ct);
        if (accepted is null || accepted.EventId != id || !await receipts.HasAcceptedEventAsync(accepted, ct)) return null;
        var registration = await executor.QuerySingleAsync<Registration>(SurrealQuery<Registration>.Key(row.RegistrationId), ct);
        if (registration is null || !registration.IsActive || registration.TenantId != row.TenantId
            || registration.DestinationRevision != row.DestinationRevision || registration.Url != row.DestinationUrl
            || registration.SecretRevision < 1 || string.IsNullOrWhiteSpace(registration.EncryptedSecret)) return null;
        var receipt = new WebhookEventRetryReceipt(command.RequestId, eventId, tenantId, row.Version + 1, row.RetryGeneration + 1, now,
            origin.Value.ToString());
        var audit = new Audit { Id = auditId, ActorId = credential.ActorUserId.ToString("N"), TenantId = tenantId.ToString("N"),
            EventId = id, RequestHash = requestHash, AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority)),
            ReceiptJson = JsonSerializer.Serialize(receipt), Reason = command.Reason.Trim(), CreatedAt = now };
        // raw: current management authority, original destination, exact undelivered epoch (lease expiry in database time) and retry receipt commit atomically.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<AZOA.WebAPI.Persistence.SurrealDb.Models.User>(tenantId.ToString("N"))
            .Where(current => current.IsActive).Set(current => current.IsActive, true).Build(), "retry_tenant", "_retry_tenant"));
        statements.Add(SurrealQuery.Of("IF $_retry_tenant = NONE OR $_retry_tenant = [] { THROW 'WEBHOOK_DELIVERY_CHANGED' }"));
        if (isOperator) statements.Add(SurrealQuery.Of("IF $_retry_auth_time < time::now() - 5m { THROW 'SIGNING_AUTHORITY_CHANGED' }")
            .WithParam("_retry_auth_time", credential.IssuedAt!.Value));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Registration>(row.RegistrationId)
            .Where(current => current.Version == registration.Version && current.IsActive && current.TenantId == row.TenantId
                && current.DestinationRevision == row.DestinationRevision && current.Url == row.DestinationUrl)
            .Set(current => current.Version, registration.Version).Build(), "retry_registration", "_retry_registration"));
        statements.Add(SurrealQuery.Of("IF $_retry_registration = NONE OR $_retry_registration = [] { THROW 'WEBHOOK_DELIVERY_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.AssignRecord(SurrealQuery<Event>.Key(id), "retry_read", "_retry_before"));
        statements.Add(SurrealQuery.Of("""
            IF $_retry_before = NONE OR ($_retry_before.lease_expires_at != NONE AND $_retry_before.lease_expires_at > time::now())
                { THROW 'WEBHOOK_DELIVERY_CHANGED' }
            """));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Event>(id)
            .Where(current => current.Version == command.ExpectedVersion && current.Status == row.Status
                && current.LeaseId == row.LeaseId && current.CycleAttemptCount == row.CycleAttemptCount
                && current.ReceiptId == row.ReceiptId && current.BodyJson == row.BodyJson)
            .Set(current => current.Version, receipt.Version).Set(current => current.Status, Event.DeliveryStatus.Pending)
            .Set(current => current.RetryGeneration, receipt.RetryGeneration).Set(current => current.CycleAttemptCount, 0L)
            .Set(current => current.NextAttemptAt, now).Unset(current => current.LastError)
            .Unset(current => current.AttemptSecretRevision).Unset(current => current.AttemptEncryptedSecret)
            .Unset(current => current.LeaseId).Unset(current => current.LeaseExpiresAt).Build(), "retry_event", "_retry_event"));
        statements.Add(SurrealQuery.Of("IF $_retry_event = NONE OR $_retry_event = [] { THROW 'WEBHOOK_DELIVERY_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(audit), "retry_audit"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            if (await CommitAsync(query, ct)) return receipt;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)) { }
        prior = await executor.QuerySingleAsync<Audit>(SurrealQuery<Audit>.Key(auditId), ct);
        return prior is null ? null : Replay(prior, requestHash);
    }

    private static WebhookEventRetryReceipt? Replay(Audit audit, string hash)
        => audit.RequestHash == hash ? JsonSerializer.Deserialize<WebhookEventRetryReceipt>(audit.ReceiptJson) : null;

    private WebhookRetryOrigin? RetryOrigin(Event row, DateTimeOffset now)
        => WebhookRetryEligibility.Classify(row.Status.ToString(), row.LeaseId, row.LeaseExpiresAt, row.CycleAttemptCount, MaximumAttempts, now);

    private WebhookEventStatus Status(Event row)
        => new(SurrealRecordGuid.Parse(row.Id), row.EventType, SurrealRecordGuid.Parse(row.RunId), SurrealRecordGuid.Parse(row.NodeId),
            row.Status.ToString(), row.Version, row.AttemptCount, row.CycleAttemptCount, row.RetryGeneration, row.OccurredAt,
            row.NextAttemptAt, row.LastError is null ? null : row.LastError.Length <= 128
                && row.LastError.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ? row.LastError : "LEGACY_DELIVERY_ERROR",
            SurrealRecordGuid.ParseOptional(row.RegistrationId), row.DestinationRevision, row.LeaseId is not null && row.LeaseExpiresAt > DateTimeOffset.UtcNow,
            RetryOrigin(row, DateTimeOffset.UtcNow) is not null);
}
