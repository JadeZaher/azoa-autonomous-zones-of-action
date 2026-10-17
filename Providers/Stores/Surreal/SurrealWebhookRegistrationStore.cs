using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using SurrealForge.Client.Idempotency;
using AZOA.WebAPI.Core.Webhooks;
using AZOA.WebAPI.Services.Webhooks;
using Row = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistration;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealWebhookRegistrationStore(ISurrealExecutor executor, WebhookSecretProtector secrets,
    WebhookSsrfGuard urls) : IWebhookRegistrationStore
{
    /// <inheritdoc/>
    public async Task<AZOAResult<WebhookRegistration>> UpsertAsync(WebhookRegistration registration, CancellationToken ct = default)
    {
        if (registration.Id == Guid.Empty || registration.TenantId == Guid.Empty || !urls.IsAllowed(registration.Url, out _))
            return new() { IsError = true, Message = "A tenant, registration identity and permitted HTTPS destination are required." };
        if (!string.IsNullOrEmpty(registration.Secret) && (string.IsNullOrWhiteSpace(registration.Secret)
            || registration.Secret.Length is < 32 or > 512))
            return new() { IsError = true, Message = "Webhook secrets must contain 32 to 512 characters." };
        var current = await ReadAsync(registration.TenantId, ct);
        if (current is not null && IsLegacy(current))
            return new() { IsError = true, Message = "Legacy registration requires explicit replacement before use." };
        if (current is not null && (SurrealRecordGuid.Parse(current.Id) != registration.Id
            || current.Version != registration.Version))
            return new() { IsError = true, Message = "Registration changed; reload before updating." };
        if (current is null && registration.Version != 0)
            return new() { IsError = true, Message = "Registration no longer exists." };
        var secretRevision = current?.SecretRevision ?? 1;
        var encrypted = current?.EncryptedSecret;
        if (!string.IsNullOrEmpty(registration.Secret))
        {
            if (current is not null) secretRevision++;
            encrypted = secrets.Protect(registration.TenantId, registration.Id, secretRevision, registration.Secret);
        }
        if (string.IsNullOrEmpty(encrypted))
            return new() { IsError = true, Message = "A new registration requires a secret." };
        var row = new Row
        {
            Id = registration.Id.ToString("N"), TenantId = SurrealLink.ToLink("user", registration.TenantId.ToString("N"))!,
            Url = registration.Url, EncryptedSecret = encrypted, Version = (current?.Version ?? 0) + 1,
            DestinationRevision = current is null ? 1 : current.DestinationRevision + (current.Url == registration.Url ? 0 : 1),
            SecretRevision = secretRevision, IsActive = registration.IsActive,
            SecretRotatedAt = current is not null && secretRevision != current.SecretRevision ? DateTimeOffset.UtcNow : current?.SecretRotatedAt,
            CreatedAt = current?.CreatedAt ?? DateTimeOffset.UtcNow,
        };
        if (current is null)
        {
            try
            {
                var create = SurrealWriter.Create(row);
                var created = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
                {
                    var response = await executor.ExecuteAsync(create, ct);
                    response.EnsureAllOk();
                    return response;
                }, ct);
                return new() { Result = ToDomain(created.GetValues<Row>(0).Single()) };
            }
            catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
            {
                return new() { IsError = true, Message = "Registration was created concurrently; reload." };
            }
        }
        var update = SurrealWriter.UpdateOnly<Row>(row.Id).Where(item => item.Version == current.Version && item.TenantId == current.TenantId)
            .Set(item => item.Url, row.Url).Set(item => item.EncryptedSecret, row.EncryptedSecret)
            .Set(item => item.Version, row.Version).Set(item => item.DestinationRevision, row.DestinationRevision)
            .Set(item => item.SecretRevision, row.SecretRevision).Set(item => item.IsActive, row.IsActive);
        update = row.SecretRotatedAt is null ? update.Unset(item => item.SecretRotatedAt) : update.Set(item => item.SecretRotatedAt, row.SecretRotatedAt);
        var mutation = update.Build();
        var result = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await executor.ExecuteAsync(mutation, ct);
            response.EnsureAllOk();
            return response;
        }, ct);
        var saved = result.GetValues<Row>(0).SingleOrDefault();
        return saved is null ? new() { IsError = true, Message = "Registration changed; reload before updating." } : new() { Result = ToDomain(saved) };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<WebhookRegistration>> GetByTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var row = await ReadAsync(tenantId, ct);
        return new() { Result = row is null ? null : ToDomain(row) };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> RotateSecretAsync(Guid tenantId, string newSecret, DateTime now, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newSecret) || newSecret.Length is < 32 or > 512)
            return new() { IsError = true, Message = "Webhook secrets must contain 32 to 512 characters." };
        var current = await GetByTenantAsync(tenantId, ct);
        if (current.Result is null) return new() { Result = false };
        current.Result.Secret = newSecret;
        var result = await UpsertAsync(current.Result, ct);
        return new() { Result = !result.IsError, IsError = result.IsError, Message = result.Message };
    }

    private Task<Row?> ReadAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = SurrealLink.ToLink("user", tenantId.ToString("N"))!;
        return executor.QuerySingleAsync<Row>(SurrealQuery<Row>.From().Where(row => row.TenantId == tenant).Limit(1), ct);
    }

    private WebhookRegistration ToDomain(Row row) => new()
    {
        Id = SurrealRecordGuid.Parse(row.Id),
        TenantId = SurrealRecordGuid.Parse(row.TenantId),
        LegacyFingerprint = IsLegacy(row) ? LegacyFingerprint(row) : null,
        Url = row.Url, EncryptedSecret = row.EncryptedSecret, Version = row.Version,
        DestinationRevision = row.DestinationRevision, SecretRevision = row.SecretRevision,
        SecretRotatedAt = row.SecretRotatedAt?.UtcDateTime, IsActive = row.IsActive, CreatedAt = row.CreatedAt.UtcDateTime,
    };
}
