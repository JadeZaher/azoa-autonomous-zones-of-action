using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for <see cref="WebhookRegistration"/> records
/// (tenant-consent-delegation §4, AC7). Tenant-scoped: a tenant only ever reads/writes
/// its OWN registration (H5 — strict per-tenant isolation). One active registration per
/// tenant for v1 (the unique <c>tenant_id</c> index enforces it).
///
/// <para><b>No-throw contract.</b> Every method returns an <see cref="AZOAResult{T}"/>
/// and captures exceptions rather than throwing.</para>
/// </summary>
public interface IWebhookRegistrationStore
{
    /// <summary>Insert or update a tenant's registration. The caller supplies a
    /// <c>TenantId</c> derived from the authenticated API-key principal — never a request
    /// body field.</summary>
    Task<AZOAResult<WebhookRegistration>> UpsertAsync(WebhookRegistration registration, CancellationToken ct = default);

    /// <summary>The tenant's own registration (or <c>Result == null</c> when none exists).
    /// Used by the delivery worker to resolve a tenant's url + secret, and by the
    /// management surface to read back. Tenant-scoped — there is no way to read another
    /// tenant's registration through this seam.</summary>
    Task<AZOAResult<WebhookRegistration>> GetByTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Rotate the tenant's HMAC secret: conditional <c>UPDATE … WHERE tenant_id=$tenant</c>
    /// SET <c>secret = newSecret, secret_rotated_at = now</c>. Scoped to the tenant's own
    /// registration. Returns whether exactly one row changed. A rotation re-keys all
    /// SUBSEQUENT deliveries; in-flight retries pick up the new secret on their next
    /// attempt (the worker reloads the registration per delivery).
    /// </summary>
    Task<AZOAResult<bool>> RotateSecretAsync(Guid tenantId, string newSecret, DateTime now, CancellationToken ct = default);
}
