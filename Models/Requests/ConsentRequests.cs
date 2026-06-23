namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Body for <c>POST /api/avatar/consent</c> (tenant-consent-delegation §1). The
/// user authorizes <see cref="TenantId"/> for <see cref="Scopes"/>. There is
/// intentionally NO grantor field — the grantor is ALWAYS the authenticated
/// user, sourced from the token's subject claim (AC9 IDOR); any grantor in a
/// request body is ignored by construction because it cannot be expressed here.
///
/// The ArdaNova join flow calls this with the USER's own token (never the tenant
/// API-key alone — H4); the brand-level UX hides the crypto, but the user's
/// credential is what authorizes the grant.
/// </summary>
public class GrantConsentRequest
{
    /// <summary>The tenant the user is authorizing.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The scopes the user grants the tenant (e.g. <c>quest:execute</c>,
    /// <c>swap:sign</c>). A hard ceiling — no later request can widen it (M3).</summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// Grant origin: <c>"UserExplicit"</c> (default) or <c>"Participation"</c>.
    /// A <c>Participation</c> grant MUST NOT carry any value-signing scope (H4);
    /// it is the join-driven standing grant tied to <see cref="ParticipationRef"/>.
    /// Parsed case-insensitively; an unrecognized value is rejected.
    /// </summary>
    public string? Origin { get; set; }

    /// <summary>Opaque ArdaNova participation id. Required when
    /// <see cref="Origin"/> is <c>Participation</c>; ignored otherwise.</summary>
    public string? ParticipationRef { get; set; }

    /// <summary>Optional expiry. Null = until revoked. A past value is rejected.</summary>
    public DateTime? ExpiresAt { get; set; }
}
