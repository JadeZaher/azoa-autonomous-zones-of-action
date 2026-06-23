using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for <see cref="ConsentGrant"/> records
/// (tenant-consent-delegation §1). Every query is scoped so a cross-tenant or
/// cross-user probe returns nothing (the isolation crux — AC9/L2/L3).
/// </summary>
public interface IConsentGrantStore
{
    /// <summary>Insert or update a grant.</summary>
    Task<AZOAResult<ConsentGrant>> UpsertAsync(ConsentGrant grant, CancellationToken ct = default);

    /// <summary>Loads a single grant by id, with no scoping — callers MUST apply the
    /// grantor/tenant scope themselves before exposing it.</summary>
    Task<AZOAResult<ConsentGrant>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>User-scoped list (AC1, <c>GET /api/avatar/consent</c>): only grants
    /// THIS user made. A cross-user probe yields an empty list, never another user's
    /// grants.</summary>
    Task<AZOAResult<IEnumerable<ConsentGrant>>> ListByGrantorAsync(Guid grantorAvatarId, CancellationToken ct = default);

    /// <summary>Tenant-scoped list (AC1/L2, <c>GET /api/tenant/consent</c>): only
    /// grants made TO this tenant. A tenant can never receive another tenant's or a
    /// non-grantee's grants.</summary>
    Task<AZOAResult<IEnumerable<ConsentGrant>>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// AC4/AC5: the LIVE lookup the signing seam uses — the single grant (if any)
    /// for <c>(grantor, tenant)</c> that COVERS <paramref name="scope"/> and is live
    /// at <paramref name="now"/> (not revoked, not expired). Returns
    /// <c>Result == null</c> with no error when no covering grant exists, so the seam
    /// fails closed. Server-side scoped — the grantor and tenant come from trusted
    /// context, never a request body.
    /// </summary>
    Task<AZOAResult<ConsentGrant>> FindCoveringGrantAsync(
        Guid grantorAvatarId, Guid tenantId, string scope, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// L3 offboard: the grants for <c>(tenant, participationRef)</c> EXACT-match,
    /// scoped to the tenant's OWN grants only (no cross-tenant ref collision, no loose
    /// match). Used to revoke a participation grant on offboarding.
    /// </summary>
    Task<AZOAResult<IEnumerable<ConsentGrant>>> ListByTenantAndParticipationRefAsync(
        Guid tenantId, string participationRef, CancellationToken ct = default);

    /// <summary>
    /// user-sovereign-identity AC3b (security-review fix): revoke EVERY live grant this
    /// user made, stamping <c>RevokedAt = now</c> on each. Called when a user CLAIMS
    /// their avatar — the residual tenant authority over a now-self-sovereign user must
    /// die the instant custody is severed, so a stale child JWT (whose nbf may predate
    /// the claim watermark) finds no covering live grant at the signing seam. Scoped to
    /// the grantor's OWN grants only. Returns the count revoked.
    /// </summary>
    Task<AZOAResult<int>> RevokeAllByGrantorAsync(Guid grantorAvatarId, DateTime now, CancellationToken ct = default);
}
