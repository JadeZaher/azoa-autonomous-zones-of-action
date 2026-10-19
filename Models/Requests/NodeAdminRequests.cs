namespace AZOA.WebAPI.Models.Requests;

/// <summary><c>POST /api/node-admin/auth/challenge</c>.</summary>
public sealed class NodeAdminChallengeRequest
{
    public string Purpose { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
}

/// <summary><c>POST /api/node-admin/auth/session</c>.</summary>
public sealed class NodeAdminSessionRequest
{
    public string ChallengeId { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string? Totp { get; set; }
}

/// <summary><c>POST /api/node-admin/admins</c>.</summary>
public sealed class NodeAdminCreateRequest
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> RoleIds { get; set; } = [];
}

/// <summary><c>PUT /api/node-admin/admins/{id}/roles</c>.</summary>
public sealed class NodeAdminAssignRolesRequest
{
    public List<string> RoleIds { get; set; } = [];
}

/// <summary><c>POST /api/node-admin/admins/{id}/activate</c>: the fingerprint confirmed out of band.</summary>
public sealed class NodeAdminActivateRequest
{
    public string Fingerprint { get; set; } = string.Empty;
}

/// <summary><c>POST /api/node-admin/admins/{id}/keys</c>: the new key signs the add_key message (proof of possession).</summary>
public sealed class NodeAdminAddKeyRequest
{
    public string PublicKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}

/// <summary><c>POST /api/node-admin/enroll/start</c>.</summary>
public sealed class NodeAdminEnrollStartRequest
{
    public string EnrollmentToken { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

/// <summary><c>POST /api/node-admin/enroll/complete</c>.</summary>
public sealed class NodeAdminEnrollCompleteRequest
{
    public string EnrollmentId { get; set; } = string.Empty;
    public string Totp { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}

/// <summary><c>POST /api/node-admin/roles</c> and <c>PUT /api/node-admin/roles/{id}</c>.</summary>
public sealed class NodeAdminRoleRequest
{
    public string Name { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = [];
}
