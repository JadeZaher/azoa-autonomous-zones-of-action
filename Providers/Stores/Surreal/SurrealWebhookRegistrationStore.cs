using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IWebhookRegistrationStore"/> (tenant-consent-delegation
/// §4, AC7). Inline-POCO style of <see cref="SurrealConsentGrantStore"/>: Guid("N")
/// lowercase-hex record ids, a record-link column for the tenant. Tenant-scoped — every
/// read/write is keyed by the tenant's own id (H5 — strict per-tenant isolation). One
/// active registration per tenant for v1 (the unique <c>tenant_id</c> index enforces it
/// at the schema level).
///
/// <para><b>No-throw.</b> Every method captures exceptions into an
/// <see cref="AZOAResult{T}"/> rather than throwing.</para>
/// </summary>
public sealed class SurrealWebhookRegistrationStore : IWebhookRegistrationStore
{
    private const string Table = "webhook_registration";

    private readonly ISurrealExecutor _executor;

    public SurrealWebhookRegistrationStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<AZOAResult<WebhookRegistration>> UpsertAsync(WebhookRegistration registration, CancellationToken ct = default)
    {
        try
        {
            if (registration.Id == Guid.Empty) registration.Id = Guid.NewGuid();
            var poco = FromDomain(registration);
            var q = SurrealWriter.Upsert(poco);
            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();
            var saved = resp.GetValues<WebhookRegistrationPoco>(0).FirstOrDefault();
            return new AZOAResult<WebhookRegistration>
            {
                Result = saved is not null ? ToDomain(saved) : registration,
                Message = "Saved.",
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<WebhookRegistration>().CaptureException(ex, $"SurrealWebhookRegistrationStore.UpsertAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<WebhookRegistration>> GetByTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            // Tenant-scoped lookup — keyed by the tenant's own id. There is no way to
            // read another tenant's registration through this seam (H5).
            var q = SurrealQuery
                .Of("SELECT * FROM webhook_registration WHERE tenant_id = $_tenant LIMIT 1")
                .WithParam("_tenant", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(tenantId)));
            var row = await _executor.QuerySingleAsync<WebhookRegistrationPoco>(q, ct);
            return new AZOAResult<WebhookRegistration>
            {
                Result = row is null ? null : ToDomain(row),
                Message = row is null ? "No registration." : "Success",
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<WebhookRegistration>().CaptureException(ex, $"SurrealWebhookRegistrationStore.GetByTenantAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> RotateSecretAsync(Guid tenantId, string newSecret, DateTime now, CancellationToken ct = default)
    {
        try
        {
            var nowUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);
            // Tenant-scoped conditional UPDATE — only the tenant's own row is touched.
            // A rotation re-keys all SUBSEQUENT deliveries; in-flight retries pick up
            // the new secret on their next attempt (the worker reloads per delivery).
            var q = SurrealQuery
                .Of("UPDATE webhook_registration SET secret = $_secret, secret_rotated_at = $_now WHERE tenant_id = $_tenant RETURN AFTER")
                .WithParam("_secret", newSecret)
                .WithParam("_now", nowUtc)
                .WithParam("_tenant", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(tenantId)));

            var resp = await _executor.ExecuteAsync(q, ct);
            if (resp.Count == 0 || !resp[0].IsOk)
                return new AZOAResult<bool> { Result = false, Message = "No-op." };
            return new AZOAResult<bool> { Result = resp[0].AffectedCount() == 1, Message = "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealWebhookRegistrationStore.RotateSecretAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ────────────────────────────────────────────────────────────────


    private static WebhookRegistrationPoco FromDomain(WebhookRegistration r) => new()
    {
        Id              = SurrealId.ToSurrealId(r.Id),
        TenantId        = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(r.TenantId)) ?? string.Empty,
        Url             = r.Url ?? string.Empty,
        Secret          = r.Secret ?? string.Empty,
        SecretRotatedAt = r.SecretRotatedAt.HasValue
                          ? new DateTimeOffset(DateTime.SpecifyKind(r.SecretRotatedAt.Value, DateTimeKind.Utc))
                          : null,
        IsActive        = r.IsActive,
        CreatedAt       = new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
    };

    private static WebhookRegistration ToDomain(WebhookRegistrationPoco p) => new()
    {
        Id              = SurrealId.FromSurrealId(StripIdPrefix(p.Id)),
        TenantId        = SurrealId.FromSurrealId(SurrealLink.FromLink(p.TenantId)!),
        Url             = p.Url ?? string.Empty,
        Secret          = p.Secret ?? string.Empty,
        SecretRotatedAt = p.SecretRotatedAt?.UtcDateTime,
        IsActive        = p.IsActive,
        CreatedAt       = p.CreatedAt.UtcDateTime,
    };

    private static string StripIdPrefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var colon = raw.IndexOf(':');
        return colon >= 0 && colon < raw.Length - 1 ? raw[(colon + 1)..] : raw;
    }

    // ── POCO (private; inline until source-gen catches up) ─────────────────────

    private sealed class WebhookRegistrationPoco : SurrealForge.Client.ISurrealRecord
    {
        public string SchemaName => Table;

        [JsonPropertyName("id")]                public string Id { get; set; } = string.Empty;
        [JsonPropertyName("tenant_id")]         public string TenantId { get; set; } = string.Empty;
        [JsonPropertyName("url")]               public string Url { get; set; } = string.Empty;
        [JsonPropertyName("secret")]            public string Secret { get; set; } = string.Empty;
        [JsonPropertyName("secret_rotated_at")] public DateTimeOffset? SecretRotatedAt { get; set; }
        [JsonPropertyName("is_active")]         public bool IsActive { get; set; }
        [JsonPropertyName("created_at")]        public DateTimeOffset CreatedAt { get; set; }
    }
}
