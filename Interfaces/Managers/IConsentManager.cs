using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// The consent authority (tenant-consent-delegation §1). A tenant may act for a
/// user ONLY within scopes the user has granted via a live
/// <see cref="ConsentGrant"/>, and the user can revoke at any time. This manager
/// owns the grant/revoke/list surface; the signing seam enforces the live check
/// (AC4/AC5, owned elsewhere).
///
/// IDOR / isolation contract (AC9, L2, L3): the <c>grantorAvatarId</c> on every
/// user-surface method is ALWAYS the authenticated USER — the controller fills it
/// from the user's token subject, NEVER from a request body. A cross-user or
/// cross-tenant probe is reported as
/// <see cref="TenantAuthorizationError.NotFound"/> (→ 404), never Forbidden, so a
/// prober cannot distinguish "no such grant" from "exists but belongs to someone
/// else" (mirrors <see cref="ITenantManager"/>'s isolation crux).
/// </summary>
public interface IConsentManager
{
    /// <summary>
    /// Grants <paramref name="tenantId"/> the given <paramref name="scopes"/> on
    /// behalf of <paramref name="grantorAvatarId"/> (the AUTHENTICATED user — never
    /// a body field, AC9). <paramref name="tenantId"/> is the tenant the user
    /// chooses to authorize (a body field is acceptable for the tenant id since the
    /// user is choosing whom to authorize — but the GRANTOR is always the principal).
    ///
    /// H4 (AC6): when <paramref name="origin"/> is
    /// <see cref="GrantOrigin.Participation"/>, the scopes MUST be the minimum
    /// non-value set — any scope in <see cref="Core.AzoaScopes.ValueSigningScopes"/>
    /// is REJECTED (value-signing requires a deliberate <c>UserExplicit</c> grant).
    /// For <c>UserExplicit</c>, value scopes are allowed.
    ///
    /// Persists the grant and writes a <see cref="ConsentAuditAction.Granted"/>
    /// audit row (AC10/L1).
    /// </summary>
    Task<AZOAResult<ConsentGrant>> GrantAsync(
        Guid grantorAvatarId,
        Guid tenantId,
        IEnumerable<string> scopes,
        GrantOrigin origin,
        string? participationRef,
        DateTime? expiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// Participation-grant convenience for the join flow (AC6/H4): identical to
    /// <see cref="GrantAsync"/> with <see cref="GrantOrigin.Participation"/>,
    /// asserting the value-scope exclusion. The CONTROLLER authenticates this as
    /// the USER (the user's token) — a tenant API-key principal alone CANNOT call
    /// it to fabricate a grant for a user it lacks a user-authenticated assertion
    /// for. <paramref name="participationRef"/> is required.
    /// </summary>
    Task<AZOAResult<ConsentGrant>> GrantParticipationAsync(
        Guid grantorAvatarId,
        Guid tenantId,
        string participationRef,
        IEnumerable<string> scopes,
        CancellationToken ct = default);

    /// <summary>
    /// Revokes a grant the user owns. Loads by id; if the grant's
    /// <see cref="ConsentGrant.GrantorAvatarId"/> != <paramref name="grantorAvatarId"/>
    /// returns <see cref="TenantAuthorizationError.NotFound"/> (AC9 — a cross-user
    /// probe is indistinguishable from "no such grant", never Forbidden). Sets
    /// <see cref="ConsentGrant.RevokedAt"/> = now and persists; the grant is inert
    /// the instant this is written (the seam re-checks live on the next sign, AC5).
    /// Writes a <see cref="ConsentAuditAction.Revoked"/> audit row.
    /// </summary>
    Task<AZOAResult<bool>> RevokeAsync(Guid grantorAvatarId, Guid grantId, CancellationToken ct = default);

    /// <summary>User-scoped list (AC1): only this user's own grants.</summary>
    Task<AZOAResult<IEnumerable<ConsentGrant>>> ListForUserAsync(Guid grantorAvatarId, CancellationToken ct = default);

    /// <summary>
    /// User-scoped status query (AC9): loads one grant, returning
    /// <see cref="TenantAuthorizationError.NotFound"/> when the grantor does not
    /// match — a cross-user probe is indistinguishable from a miss.
    /// </summary>
    Task<AZOAResult<ConsentGrant>> GetForUserAsync(Guid grantorAvatarId, Guid grantId, CancellationToken ct = default);

    /// <summary>Tenant-scoped list (AC1/L2): only grants made TO this tenant. A
    /// tenant can never receive another tenant's or a non-grantee's grants.</summary>
    Task<AZOAResult<IEnumerable<ConsentGrant>>> ListForTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Tenant offboard (L3): revokes the participation grants for
    /// <c>(tenantId, participationRef)</c> EXACT-match, scoped to the tenant's OWN
    /// grants only (no cross-tenant ref collision, no loose match). Authed as the
    /// TENANT (it offboards a participation it owns). Sets
    /// <see cref="ConsentGrant.RevokedAt"/> on each and writes a
    /// <see cref="ConsentAuditAction.Revoked"/> row per grant. Returns the count
    /// revoked (0 when no matching live grant exists — not an error).
    /// </summary>
    Task<AZOAResult<int>> RevokeByParticipationAsync(Guid tenantId, string participationRef, CancellationToken ct = default);
}
