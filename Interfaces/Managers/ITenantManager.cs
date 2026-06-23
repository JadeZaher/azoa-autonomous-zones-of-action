using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// Discriminator the controller uses to translate a manager auth failure into
/// the right HTTP status without raw string-matching. Carried via the
/// <see cref="AZOAResult{T}.Message"/> prefix — mirrors
/// <see cref="STARODKAuthorizationError"/>.
///
/// SECURITY NOTE: a cross-tenant or unowned target is reported as
/// <see cref="NotFound"/> (→ 404), NEVER <see cref="Forbidden"/>, so a prober
/// cannot distinguish "no such avatar" from "exists but belongs to another
/// tenant" (the §3 isolation crux). <see cref="Forbidden"/> is reserved for the
/// surface-level "missing tenant:provision scope" case, which the
/// authorization policy already rejects at 403 before the manager runs.
/// </summary>
public static class TenantAuthorizationError
{
    /// <summary>Caller lacks tenant authority — surfaces as 403.</summary>
    public const string Forbidden = "TENANT_FORBIDDEN: ";

    /// <summary>
    /// Target child does not exist OR is not owned by this tenant — surfaces as
    /// 404 (deliberately indistinguishable, per the isolation threat model).
    /// </summary>
    public const string NotFound = "TENANT_NOT_FOUND: ";
}

/// <summary>
/// Tenant provisioning surface. A "tenant" is an Avatar that owns an API key
/// carrying the <c>tenant:provision</c> scope (Decision D1 — Option B: scope +
/// <c>OwnerTenantId</c> self-FK, no parallel Tenant entity). Every method takes
/// the tenant id as an explicit parameter the controller fills from the
/// authenticated key's claim — NEVER from a request body (IDOR rule, mirrors
/// <c>AvatarManager.UpdateAsync(id, model, avatarId)</c>).
/// </summary>
public interface ITenantManager
{
    /// <summary>
    /// user-sovereign-identity HARD CUTOVER (AC6): provisions a SELF-OWNED avatar
    /// (<c>OwnerTenantId = null</c>) the user can later CLAIM — NOT a tenant-locked
    /// child. There is no longer any path that permanently locks an avatar to a
    /// tenant. The avatar is recorded with the tenant's <c>ExternalUserId</c> for
    /// onboarding correlation only; it grants the tenant NO standing authority — a
    /// tenant acts for the user solely through a live <c>ConsentGrant</c>
    /// (see <see cref="IssueChildCredentialAsync"/>). Idempotent on
    /// <c>(tenantId, externalUserId)</c> correlation.
    /// </summary>
    Task<AZOAResult<ChildAvatarResponse>> ProvisionChildAsync(Guid tenantId, ProvisionChildModel model, CancellationToken ct = default);

    /// <summary>
    /// Lists the tenant's child avatars, optionally filtered to a single
    /// external user id. Scoped to the tenant — never returns another tenant's
    /// children.
    /// </summary>
    Task<AZOAResult<IEnumerable<ChildAvatarResponse>>> ListChildrenAsync(Guid tenantId, string? externalUserId, CancellationToken ct = default);

    /// <summary>
    /// Resolves one child by the tenant's own external user id (the primary
    /// tenant lookup path). Cross-tenant / no match → <see cref="TenantAuthorizationError.NotFound"/>.
    /// </summary>
    Task<AZOAResult<ChildAvatarResponse>> ResolveChildAsync(Guid tenantId, string externalUserId, CancellationToken ct = default);

    /// <summary>
    /// Issues a short-lived child-scoped JWT (subject = the USER's avatar id, plus
    /// an <c>act_as_tenant</c> claim = <paramref name="tenantId"/>) the tenant uses
    /// to act FOR that user.
    /// <para>
    /// tenant-consent-delegation AC2/M2 — NO OWNERSHIP-ONLY PATH: issuance ALWAYS
    /// requires a LIVE <c>ConsentGrant</c> (grantor = the user, tenant =
    /// <paramref name="tenantId"/>, scope ⊇ requested, not revoked/expired). The
    /// legacy <c>child.OwnerTenantId == tenantId</c> check is GONE (the hard cutover
    /// makes every avatar self-owned). A target with no covering live grant returns
    /// <see cref="TenantAuthorizationError.NotFound"/> (404, never 403 — the
    /// isolation crux).
    /// </para>
    /// <para>
    /// M3 scope ceiling: the issued scopes = (server-trusted
    /// <paramref name="tenantScopes"/>) ∩ (granted scopes) ∩
    /// (<paramref name="requestedScopes"/>) — granted scopes are a hard ceiling no
    /// request field can widen. AC3b: if the user's <c>AuthNotBefore</c> watermark
    /// is set, the issued token's <c>nbf</c> is at/after it, and a credential
    /// referencing a pre-claim state is rejected.
    /// </para>
    /// </summary>
    Task<AZOAResult<ChildCredentialResponse>> IssueChildCredentialAsync(
        Guid tenantId,
        Guid childId,
        IEnumerable<string> requestedScopes,
        IEnumerable<string> tenantScopes,
        CancellationToken ct = default);
}
