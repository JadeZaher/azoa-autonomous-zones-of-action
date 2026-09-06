using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
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
/// <para><b>Custodial onboarding correlation.</b> <see cref="ProvisionChildAsync"/>
/// binds an unclaimed user to the provisioning tenant so the tenant/external-user
/// pair is unique and retry-safe. Claiming clears <c>OwnerTenantId</c>, while the
/// deterministic id remains a create-only correlation so a retry cannot reclaim
/// or reset the account. It never grants signing authority, which still requires
/// a live consent grant.</para>
///
/// <para><b>Consent-gated issuance (tenant-consent-delegation AC2/M2).</b>
/// <see cref="IssueChildCredentialAsync"/> ALWAYS requires a LIVE
/// <see cref="Models.ConsentGrant"/> (grantor = the user, tenant = the caller,
/// scope ⊇ requested). The legacy <c>OwnerTenantId == tenantId</c> ownership-only
/// path is REMOVED — there is no credential issued on ownership alone. A target
/// with no covering live grant returns <see cref="TenantAuthorizationError.NotFound"/>
/// (404, never 403 — the isolation crux).</para>
///
/// <para>The issued child JWT carries the USER's user id as subject PLUS an
/// <c>act_as_tenant</c> claim (the tenant id) so a tenant-driven action is
/// DISTINGUISHABLE from a user-driven one at the signing seam (C1); and its
/// <c>nbf</c> respects the user's <c>AuthNotBefore</c> watermark so a token minted
/// before a claim cannot act after it (AC3b).</para>
/// </summary>
public class TenantManager : ITenantManager
{
    /// <summary>Short-lived child credential TTL (D2: shorten vs. the 24h login token).</summary>
    private static readonly TimeSpan ChildTokenLifetime = TimeSpan.FromMinutes(15);

    private readonly IUserStore _userStore;
    private readonly IConfiguration _config;
    private readonly IConsentGrantStore _consentGrants;

    public TenantManager(
        IUserStore userStore,
        IConfiguration config,
        IConsentGrantStore consentGrants)
    {
        _userStore = userStore;
        _config = config;
        _consentGrants = consentGrants;
    }

    public async Task<AZOAResult<ChildUserResponse>> ProvisionChildAsync(Guid tenantId, ProvisionChildModel model, CancellationToken ct = default)
    {
        var result = new AZOAResult<ChildUserResponse>();

        if (string.IsNullOrWhiteSpace(model.ExternalUserId)
            || model.ExternalUserId.Trim().Length > 128
            || model.ExternalUserId.Any(char.IsControl))
        {
            result.IsError = true;
            result.Message = "externalUserId must be a non-empty value of at most 128 characters.";
            return result;
        }

        var externalUserId = model.ExternalUserId.Trim();
        var deterministicId = DeterministicUserId(tenantId, externalUserId);

        var existing = await _userStore.GetByTenantAndExternalUserAsync(tenantId, externalUserId, ct);
        if (existing.IsError)
            return new AZOAResult<ChildUserResponse>
            {
                IsError = true,
                Message = SafePersistenceMessage(existing.Message)
            };
        if (existing.Result is not null)
            return new AZOAResult<ChildUserResponse>
            {
                Result = ToResponse(existing.Result),
                Message = "User already provisioned for this tenant user."
            };

        // Claim severs OwnerTenantId by design. Resolve the immutable deterministic
        // id before create so a post-claim retry returns the original user account
        // and can never overwrite credentials or reattach tenant ownership.
        var deterministicExisting = await _userStore.GetByIdAsync(deterministicId, ct);
        if (!deterministicExisting.IsError && deterministicExisting.Result is not null)
        {
            if (!MatchesCorrelation(deterministicExisting.Result, tenantId, externalUserId, deterministicId))
                return CorrelationConflict();

            return new AZOAResult<ChildUserResponse>
            {
                Result = ToResponse(deterministicExisting.Result),
                Message = "User already provisioned for this tenant user."
            };
        }
        if (deterministicExisting.IsError
            && IsPersistenceUnavailable(deterministicExisting.Message))
        {
            return new AZOAResult<ChildUserResponse>
            {
                IsError = true,
                Message = SafePersistenceMessage(deterministicExisting.Message)
            };
        }

        var correlationHash = CorrelationHash(tenantId, externalUserId);
        var seed = $"onboard-{Convert.ToHexString(correlationHash.AsSpan(0, 12)).ToLowerInvariant()}";
        var username = string.IsNullOrWhiteSpace(model.Username) ? seed : model.Username.Trim();
        var email = string.IsNullOrWhiteSpace(model.Email) ? $"{seed}@onboard.azoa.local" : model.Email.Trim();

        var child = new User
        {
            Id = deterministicId,
            Username = username,
            Email = email,
            // No password login path yet; the USER sets their own credential at
            // claim time (user-side, AC3). A random hash keeps the column non-empty
            // without granting a usable password and is NOT derivable by the tenant.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
            IsActive = true,
            IsVerified = false,
            // Tenant-bound until the user claims the custodial onboarding record.
            OwnerTenantId = tenantId,
            ExternalUserId = externalUserId,
            // Correlation for the tenant's claim-invite flow (not an ownership link).
            ExternalRef = string.IsNullOrWhiteSpace(model.ExternalRef)
                ? $"tenant:{tenantId:N}:{externalUserId}"
                : model.ExternalRef.Trim(),
        };

        var saved = await _userStore.CreateIfAbsentAsync(child, ct);
        if (saved.IsError || saved.Result is null)
        {
            result.IsError = true;
            result.Message = saved.IsError
                ? SafePersistenceMessage(saved.Message)
                : "Failed to provision user.";
            return result;
        }

        if (!MatchesCorrelation(saved.Result, tenantId, externalUserId, deterministicId))
            return CorrelationConflict();

        result.Result = ToResponse(saved.Result);
        result.Message = "Tenant-bound user provisioned (claimable by the user).";
        return result;
    }

    public async Task<AZOAResult<IEnumerable<ChildUserResponse>>> ListChildrenAsync(Guid tenantId, string? externalUserId, CancellationToken ct = default)
    {
        var result = new AZOAResult<IEnumerable<ChildUserResponse>>();

        var owned = await _userStore.ListByOwnerTenantAsync(tenantId, ct);
        if (owned.IsError)
        {
            result.IsError = true;
            result.Message = SafePersistenceMessage(owned.Message);
            return result;
        }

        var children = owned.Result ?? Enumerable.Empty<IUser>();
        if (!string.IsNullOrWhiteSpace(externalUserId))
        {
            var filter = externalUserId.Trim();
            children = children.Where(c => string.Equals(c.ExternalUserId, filter, StringComparison.Ordinal));
        }

        result.Result = children.Select(ToResponse).ToList();
        result.Message = "Success";
        return result;
    }

    public async Task<AZOAResult<ChildUserResponse>> ResolveChildAsync(Guid tenantId, string externalUserId, CancellationToken ct = default)
    {
        var result = new AZOAResult<ChildUserResponse>();

        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            // Indistinguishable-from-miss: no leak about what exists.
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No child user for that external user id.";
            return result;
        }

        externalUserId = externalUserId.Trim();
        var found = await _userStore.GetByTenantAndExternalUserAsync(tenantId, externalUserId, ct);
        if (found.IsError)
        {
            result.IsError = true;
            result.Message = SafePersistenceMessage(found.Message);
            return result;
        }
        if (found.Result is null)
        {
            // A claimed user no longer carries OwnerTenantId. Its deterministic
            // id remains the immutable, tenant-partitioned correlation key.
            var deterministicId = DeterministicUserId(tenantId, externalUserId);
            var claimed = await _userStore.GetByIdAsync(deterministicId, ct);
            if (!claimed.IsError
                && claimed.Result is not null
                && MatchesCorrelation(claimed.Result, tenantId, externalUserId, deterministicId))
            {
                result.Result = ToResponse(claimed.Result);
                result.Message = "Success";
                return result;
            }

            if (claimed.IsError && IsPersistenceUnavailable(claimed.Message))
            {
                result.IsError = true;
                result.Message = SafePersistenceMessage(claimed.Message);
                return result;
            }

            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No child user for that external user id.";
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

        // Load the user user. A missing user is NOT_FOUND (404) — the isolation
        // crux: a prober cannot distinguish "no such user" from "no grant".
        var loaded = await _userStore.GetByIdAsync(childId, ct);
        if (loaded.IsError || loaded.Result is null)
        {
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such user.";
            return result;
        }

        var user = loaded.Result;

        // Server-trusted tenant scopes (M3): the ceiling derived from the
        // authenticated tenant principal, never a request-body field. tenant:provision
        // is never delegated down (a credential must not provision further users).
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
            result.Message = TenantAuthorizationError.NotFound + "No such user.";
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
            result.Message = TenantAuthorizationError.NotFound + "No such user.";
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
            result.Message = TenantAuthorizationError.NotFound + "No such user.";
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
            UserId = user.Id,
            Token = token,
            ExpiresAt = expiresAt,
            Scopes = delegated,
        };
        result.Message = "Child credential issued (consent-gated).";
        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ChildUserResponse ToResponse(IUser a) => new()
    {
        UserId = a.Id,
        ExternalUserId = a.ExternalUserId ?? string.Empty,
        ExternalRef = a.ExternalRef,
        Username = a.Username,
        Email = a.Email,
    };

    private static byte[] CorrelationHash(Guid tenantId, string externalUserId)
        => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"azoa:tenant-user:v1:{tenantId:N}:{externalUserId}"));

    private static Guid DeterministicUserId(Guid tenantId, string externalUserId)
        => new(CorrelationHash(tenantId, externalUserId).AsSpan(0, 16));

    private static bool MatchesCorrelation(
        IUser user,
        Guid tenantId,
        string externalUserId,
        Guid deterministicId)
        => user.Id == deterministicId
            && string.Equals(user.ExternalUserId, externalUserId, StringComparison.Ordinal)
            && (user.OwnerTenantId is null || user.OwnerTenantId == tenantId);

    private static AZOAResult<ChildUserResponse> CorrelationConflict() => new()
    {
        IsError = true,
        Message = "The deterministic tenant identity is already bound to a different account."
    };

    private static bool IsPersistenceUnavailable(string? message)
        => message?.StartsWith("USER_STORE_UNAVAILABLE:", StringComparison.Ordinal) == true;

    private static string SafePersistenceMessage(string? message)
        => IsPersistenceUnavailable(message)
            ? "TENANT_IDENTITY_UNAVAILABLE: Tenant identity persistence is temporarily unavailable."
            : "TENANT_IDENTITY_ERROR: Tenant identity could not be processed.";

    /// <summary>
    /// Minimal symmetric child-token primitive. Subject = the USER's user id (so
    /// downstream per-user authorization treats it like the user); one
    /// <c>scope</c> claim per delegated scope. tenant-consent-delegation C1/AC4: a
    /// distinguishing <c>act_as_tenant</c> claim (the tenant id) marks this as
    /// tenant-driven so the signing seam runs the live consent check. AC3b: an
    /// explicit <c>nbf</c> (not-before) at/after the user's claim watermark.
    /// </summary>
    public const string ActAsTenantClaim = "act_as_tenant";

    private string GenerateChildJwt(Guid userUserId, Guid tenantId, IEnumerable<string> scopes, DateTime notBefore, DateTime expiresAt)
    {
        var key = _config.GetValue<string>("Jwt:Key") ?? throw new InvalidOperationException("JWT Key missing.");
        var securityKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userUserId.ToString()),
            new("UserId", userUserId.ToString()),
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
