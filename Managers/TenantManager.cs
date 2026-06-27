using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Tenant provisioning manager — REARCHITECTED by user-self-sovereignty (2026-06-22).
///
/// <para><b>HARD CUTOVER (user-sovereign-identity AC6).</b> A tenant no longer OWNS
/// its users. <see cref="ProvisionChildAsync"/> mints a SELF-OWNED avatar
/// (<c>OwnerTenantId = null</c>) the user can claim — never a tenant-locked child.
/// The tenant-provision-and-lock model is gone; no path here permanently locks an
/// avatar to a tenant.</para>
///
/// <para><b>Consent-gated issuance (tenant-consent-delegation AC2/M2).</b>
/// <see cref="IssueChildCredentialAsync"/> ALWAYS requires a LIVE
/// <see cref="Models.ConsentGrant"/> (grantor = the user, tenant = the caller,
/// scope ⊇ requested). The legacy <c>OwnerTenantId == tenantId</c> ownership-only
/// path is REMOVED — there is no credential issued on ownership alone. A target
/// with no covering live grant returns <see cref="TenantAuthorizationError.NotFound"/>
/// (404, never 403 — the isolation crux).</para>
///
/// <para>The issued child JWT carries the USER's avatar id as subject PLUS an
/// <c>act_as_tenant</c> claim (the tenant id) so a tenant-driven action is
/// DISTINGUISHABLE from a user-driven one at the signing seam (C1); and its
/// <c>nbf</c> respects the user's <c>AuthNotBefore</c> watermark so a token minted
/// before a claim cannot act after it (AC3b).</para>
/// </summary>
public class TenantManager : ITenantManager
{
    /// <summary>Short-lived child credential TTL (D2: shorten vs. the 24h login token).</summary>
    private static readonly TimeSpan ChildTokenLifetime = TimeSpan.FromMinutes(15);

    private readonly IAvatarStore _avatarStore;
    private readonly IConfiguration _config;
    private readonly IConsentGrantStore _consentGrants;

    public TenantManager(
        IAvatarStore avatarStore,
        IConfiguration config,
        IConsentGrantStore consentGrants)
    {
        _avatarStore = avatarStore;
        _config = config;
        _consentGrants = consentGrants;
    }

    public async Task<AZOAResult<ChildAvatarResponse>> ProvisionChildAsync(Guid tenantId, ProvisionChildModel model, CancellationToken ct = default)
    {
        var result = new AZOAResult<ChildAvatarResponse>();

        if (string.IsNullOrWhiteSpace(model.ExternalUserId))
        {
            result.IsError = true;
            result.Message = "externalUserId is required.";
            return result;
        }

        var externalUserId = model.ExternalUserId.Trim();

        // HARD CUTOVER (AC6): mint a SELF-OWNED avatar (OwnerTenantId = null), NOT a
        // tenant-locked child. The user owns it from birth and can claim a login
        // credential later (WalletAuthManager.ClaimAsync). The tenant gets NO
        // standing authority from provisioning — only a live ConsentGrant lets it
        // act. We correlate the row to the tenant's onboarding via ExternalRef
        // (tenant:{id}:{extuser}) for the claim-invite lookup, NOT via ownership.
        var seed = $"onboard-{tenantId:N}-{externalUserId}";
        var username = string.IsNullOrWhiteSpace(model.Username) ? seed : model.Username.Trim();
        var email = string.IsNullOrWhiteSpace(model.Email) ? $"{seed}@onboard.azoa.local" : model.Email.Trim();

        var child = new Avatar
        {
            Username = username,
            Email = email,
            // No password login path yet; the USER sets their own credential at
            // claim time (user-side, AC3). A random hash keeps the column non-empty
            // without granting a usable password and is NOT derivable by the tenant.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
            IsActive = true,
            IsVerified = false,
            // AC6: NEVER lock to the tenant. Self-owned from birth.
            OwnerTenantId = null,
            ExternalUserId = externalUserId,
            // Correlation for the tenant's claim-invite flow (not an ownership link).
            ExternalRef = string.IsNullOrWhiteSpace(model.ExternalRef)
                ? $"tenant:{tenantId:N}:{externalUserId}"
                : model.ExternalRef.Trim(),
        };

        var saved = await _avatarStore.UpsertAsync(child, ct);
        if (saved.IsError || saved.Result is null)
        {
            result.IsError = true;
            result.Message = saved.IsError ? saved.Message : "Failed to provision avatar.";
            result.Exception = saved.Exception;
            return result;
        }

        result.Result = ToResponse(saved.Result);
        result.Message = "Self-owned avatar provisioned (claimable; not tenant-locked).";
        return result;
    }

    public async Task<AZOAResult<IEnumerable<ChildAvatarResponse>>> ListChildrenAsync(Guid tenantId, string? externalUserId, CancellationToken ct = default)
    {
        var result = new AZOAResult<IEnumerable<ChildAvatarResponse>>();

        var owned = await _avatarStore.ListByOwnerTenantAsync(tenantId, ct);
        if (owned.IsError)
        {
            result.IsError = true;
            result.Message = owned.Message;
            result.Exception = owned.Exception;
            return result;
        }

        var children = owned.Result ?? Enumerable.Empty<IAvatar>();
        if (!string.IsNullOrWhiteSpace(externalUserId))
        {
            var filter = externalUserId.Trim();
            children = children.Where(c => string.Equals(c.ExternalUserId, filter, StringComparison.Ordinal));
        }

        result.Result = children.Select(ToResponse).ToList();
        result.Message = "Success";
        return result;
    }

    public async Task<AZOAResult<ChildAvatarResponse>> ResolveChildAsync(Guid tenantId, string externalUserId, CancellationToken ct = default)
    {
        var result = new AZOAResult<ChildAvatarResponse>();

        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            // Indistinguishable-from-miss: no leak about what exists.
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No child avatar for that external user id.";
            return result;
        }

        var found = await _avatarStore.GetByTenantAndExternalUserAsync(tenantId, externalUserId.Trim(), ct);
        if (found.IsError || found.Result is null)
        {
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No child avatar for that external user id.";
            result.Exception = found.Exception;
            return result;
        }

        result.Result = ToResponse(found.Result);
        result.Message = "Success";
        return result;
    }

    public async Task<AZOAResult<ChildCredentialResponse>> IssueChildCredentialAsync(
        Guid tenantId,
        Guid childId,
        IEnumerable<string> requestedScopes,
        IEnumerable<string> tenantScopes,
        CancellationToken ct = default)
    {
        var result = new AZOAResult<ChildCredentialResponse>();

        // Load the user avatar. A missing avatar is NOT_FOUND (404) — the isolation
        // crux: a prober cannot distinguish "no such avatar" from "no grant".
        var loaded = await _avatarStore.GetByIdAsync(childId, ct);
        if (loaded.IsError || loaded.Result is null)
        {
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such avatar.";
            return result;
        }

        var user = loaded.Result;

        // Server-trusted tenant scopes (M3): the ceiling derived from the
        // authenticated tenant principal, never a request-body field. tenant:provision
        // is never delegated down (a credential must not provision further avatars).
        var tenantOwn = new HashSet<string>(
            (tenantScopes ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
            StringComparer.Ordinal);
        tenantOwn.Remove(AzoaScopes.TenantProvision);

        var requested = (requestedScopes ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // AC2/M2 — NO OWNERSHIP-ONLY PATH. Find a LIVE ConsentGrant from this user to
        // this tenant. No grant ⇒ NOT_FOUND (404, never 403). The legacy
        // OwnerTenantId == tenantId check is GONE.
        var now = DateTime.UtcNow;
        var grantsResult = await _consentGrants.ListByGrantorAsync(user.Id, ct);
        if (grantsResult.IsError)
        {
            // Fail closed — a grant-lookup failure denies issuance.
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such avatar.";
            return result;
        }

        var liveGrantScopes = (grantsResult.Result ?? Enumerable.Empty<Models.ConsentGrant>())
            .Where(g => g.TenantId == tenantId && g.IsLiveAt(now))
            .SelectMany(g => g.Scopes)
            .ToHashSet(StringComparer.Ordinal);

        if (liveGrantScopes.Count == 0)
        {
            // No covering live grant — the tenant has no authority for this user.
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such avatar.";
            return result;
        }

        // M3 scope ceiling = (tenant scopes) ∩ (granted scopes) ∩ (requested). An
        // empty requested set delegates the full (tenant ∩ granted) intersection.
        var ceiling = tenantOwn.Where(liveGrantScopes.Contains);
        var delegated = (requested.Count == 0
                ? ceiling
                : ceiling.Where(requested.Contains))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (delegated.Count == 0)
        {
            // The grant exists but covers none of the requested/allowed scopes.
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such avatar.";
            return result;
        }

        // AC3b: the token's nbf is at/after the user's AuthNotBefore watermark, so a
        // credential cannot reference a pre-claim state.
        var notBefore = user.AuthNotBefore.HasValue && user.AuthNotBefore.Value > now
            ? user.AuthNotBefore.Value
            : now;
        var expiresAt = notBefore.Add(ChildTokenLifetime);
        var token = GenerateChildJwt(user.Id, tenantId, delegated, notBefore, expiresAt);

        result.Result = new ChildCredentialResponse
        {
            AvatarId = user.Id,
            Token = token,
            ExpiresAt = expiresAt,
            Scopes = delegated,
        };
        result.Message = "Child credential issued (consent-gated).";
        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ChildAvatarResponse ToResponse(IAvatar a) => new()
    {
        AvatarId = a.Id,
        ExternalUserId = a.ExternalUserId ?? string.Empty,
        ExternalRef = a.ExternalRef,
        Username = a.Username,
        Email = a.Email,
    };

    /// <summary>
    /// Minimal symmetric child-token primitive. Subject = the USER's avatar id (so
    /// downstream per-avatar authorization treats it like the user); one
    /// <c>scope</c> claim per delegated scope. tenant-consent-delegation C1/AC4: a
    /// distinguishing <c>act_as_tenant</c> claim (the tenant id) marks this as
    /// tenant-driven so the signing seam runs the live consent check. AC3b: an
    /// explicit <c>nbf</c> (not-before) at/after the user's claim watermark.
    /// </summary>
    public const string ActAsTenantClaim = "act_as_tenant";

    private string GenerateChildJwt(Guid userAvatarId, Guid tenantId, IEnumerable<string> scopes, DateTime notBefore, DateTime expiresAt)
    {
        var key = _config.GetValue<string>("Jwt:Key") ?? throw new InvalidOperationException("JWT Key missing.");
        var securityKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userAvatarId.ToString()),
            new("AvatarId", userAvatarId.ToString()),
            // C1/AC4: marks the token as tenant-driven; the signing seam reads this
            // to require a live consent grant before any key decrypt.
            new(ActAsTenantClaim, tenantId.ToString()),
            // token-type segregation (security-review S5): explicitly classes this as a
            // scoped tenant child credential, not a full-authority user login.
            new(AzoaClaims.TokenUse, AzoaClaims.TokenUseChild),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        foreach (var scope in scopes)
            claims.Add(new Claim("scope", scope));

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            notBefore: notBefore,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
