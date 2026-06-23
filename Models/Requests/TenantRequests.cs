namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Body for <c>POST /api/tenant/avatars</c>. Carries the tenant's own user id
/// for the child being provisioned plus optional seeds. There is intentionally
/// NO tenant id field — the tenant id is sourced exclusively from the
/// authenticated key's claim (IDOR rule); any tenant id in a request body is
/// ignored by construction because it cannot be expressed here.
/// </summary>
public class ProvisionChildModel
{
    /// <summary>The tenant's own user id for this child (unique per tenant).</summary>
    public string ExternalUserId { get; set; } = string.Empty;

    /// <summary>Free opaque tenant string (e.g. org/realm). Optional.</summary>
    public string? ExternalRef { get; set; }

    /// <summary>
    /// Optional username seed. When omitted, a deterministic username is
    /// synthesized from the tenant id + external user id (both are
    /// unique-indexed and required on the avatar record).
    /// </summary>
    public string? Username { get; set; }

    /// <summary>Optional email seed. Synthesized deterministically when omitted.</summary>
    public string? Email { get; set; }
}

/// <summary>
/// Body for <c>POST /api/tenant/avatars/{id}/credential</c>. The requested
/// scopes are intersected with the tenant key's own scopes before issuance
/// (no privilege escalation through delegation). When empty, the issued
/// credential carries the full intersection of the tenant's delegable scopes.
/// </summary>
public class IssueChildCredentialModel
{
    /// <summary>Scopes the tenant wishes to delegate onto the child credential.</summary>
    public List<string> Scopes { get; set; } = new();
}

/// <summary>Echo response for a provisioned / resolved child avatar.</summary>
public class ChildAvatarResponse
{
    public Guid AvatarId { get; set; }
    public string ExternalUserId { get; set; } = string.Empty;
    public string? ExternalRef { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

/// <summary>Response for an issued short-lived child credential.</summary>
public class ChildCredentialResponse
{
    public Guid AvatarId { get; set; }
    /// <summary>The short-lived child JWT. Subject is the child avatar id.</summary>
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    /// <summary>The scopes actually delegated (intersection of requested ∩ tenant's own).</summary>
    public List<string> Scopes { get; set; } = new();
}
