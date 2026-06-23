namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Echo response for a <see cref="AZOA.WebAPI.Models.ConsentGrant"/>
/// (tenant-consent-delegation §1). A deliberately NARROW projection: it carries
/// only the fields a user (or the authorized tenant) needs to reason about the
/// grant's identity, scope and lifecycle. It exposes NO signing material, NO key
/// reference, and NO audit detail — the grant record is an authorization fact,
/// not a credential. The grantor avatar id is intentionally OMITTED on the wire:
/// the user surface already knows it is "me", and the tenant surface must never
/// be handed the grantor as an enumerable identifier beyond what it already
/// authorized (L2 isolation).
/// </summary>
public class ConsentGrantResponse
{
    public Guid GrantId { get; set; }

    /// <summary>The tenant this grant authorizes.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The granted scopes (a hard ceiling — M3).</summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary><c>UserExplicit</c> or <c>Participation</c> as a string.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>Opaque participation reference; null for a <c>UserExplicit</c> grant.</summary>
    public string? ParticipationRef { get; set; }

    public DateTime GrantedAt { get; set; }

    /// <summary>Null = until revoked.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Set once the grant has been revoked; null while live.</summary>
    public DateTime? RevokedAt { get; set; }
}
