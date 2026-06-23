// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models;

/// <summary>
/// How a <see cref="ConsentGrant"/> came to exist (tenant-consent-delegation §1).
/// </summary>
public enum GrantOrigin
{
    /// <summary>
    /// A deliberate, user-authenticated authorization for a specific (often
    /// value-moving) action. REQUIRED for any value-signing scope
    /// (<see cref="Core.AzoaScopes.ValueSigningScopes"/>) — a value action gets an
    /// explicit grant, never a side effect of joining (H4).
    /// </summary>
    UserExplicit = 0,

    /// <summary>
    /// The join-driven standing grant: created when the user joins ArdaNova / a
    /// project, tied to a <see cref="ConsentGrant.ParticipationRef"/>, carrying only
    /// minimum non-value scopes. Offboarding revokes it. Crypto-free UX, but still
    /// bound to a user-authenticated act (H4) — the tenant API-key alone cannot
    /// fabricate it.
    /// </summary>
    Participation = 1,
}

/// <summary>
/// A durable, revocable record: the user (<see cref="GrantorAvatarId"/>) authorizes
/// a tenant (<see cref="TenantId"/>) to drive actions within <see cref="Scopes"/>,
/// optionally time-boxed (<see cref="ExpiresAt"/>), always revocable
/// (<see cref="RevokedAt"/>). This is the SOLE authority a tenant has to act for a
/// self-owned user after the [[user-sovereign-identity]] hard cutover — there is NO
/// ownership-only path (M2). The signing seam does a LIVE validity check against
/// this record before every tenant-driven key decrypt (AC4/AC5).
/// </summary>
public sealed class ConsentGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The USER who granted (must own the avatar). The grantor against whom
    /// the signing seam looks up consent. NEVER a request-body field (AC9 IDOR).</summary>
    public Guid GrantorAvatarId { get; set; }

    /// <summary>The tenant principal being authorized.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Granted scopes (e.g. <c>quest:execute</c>, <c>swap:sign</c>). A hard
    /// ceiling — no request field can widen the effective scope (M3).</summary>
    public List<string> Scopes { get; set; } = new();

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Null = until revoked. A live <c>now &gt;= ExpiresAt</c> at the seam
    /// is the expiry enforcement (M4) — NOT the webhook, NOT a background job.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Set on revoke; the grant is inert the instant this is written
    /// (AC5/AC8) — the seam re-checks it on the very next sign.</summary>
    public DateTime? RevokedAt { get; set; }

    public GrantOrigin Origin { get; set; } = GrantOrigin.UserExplicit;

    /// <summary>Opaque ArdaNova participation id; offboarding revokes by
    /// <c>(TenantId, ParticipationRef)</c> exact-match within the tenant's own grants
    /// only (L3). Null for <see cref="GrantOrigin.UserExplicit"/> grants.</summary>
    public string? ParticipationRef { get; set; }

    /// <summary>
    /// tenant-consent-delegation AC5/M4: a grant is live iff not revoked and not
    /// expired at <paramref name="now"/>. This is the predicate the seam evaluates on
    /// EVERY tenant-driven sign — the already-issued child JWT confers no standing
    /// authority.
    /// </summary>
    public bool IsLiveAt(DateTime now)
        => RevokedAt is null && (ExpiresAt is null || now < ExpiresAt.Value);

    /// <summary>
    /// True iff this grant is live at <paramref name="now"/> AND its scope set covers
    /// <paramref name="scope"/> (the operation's required signing scope). Empty/blank
    /// scope is never covered — a tenant-driven sign MUST name a concrete scope.
    /// </summary>
    public bool Covers(string scope, DateTime now)
        => IsLiveAt(now)
           && !string.IsNullOrWhiteSpace(scope)
           && Scopes.Contains(scope, StringComparer.Ordinal);
}
