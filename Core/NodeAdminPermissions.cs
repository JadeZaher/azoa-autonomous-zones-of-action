namespace AZOA.WebAPI.Core;

/// <summary>Closed node-admin permission catalog and built-in roles; see <c>Services/NodeAdmin/AGENTS.md</c> §permissions.</summary>
public static class NodeAdminPermissions
{
    public const string AdminsRead = "admins.read";
    public const string AdminsManage = "admins.manage";
    public const string RolesRead = "roles.read";
    public const string RolesManage = "roles.manage";
    public const string AuditRead = "audit.read";
    public const string GovernanceRead = "governance.read";
    public const string GovernanceWrite = "governance.write";
    public const string KycRead = "kyc.read";
    public const string KycDecide = "kyc.decide";
    public const string KycConfigure = "kyc.configure";
    public const string UsersRead = "users.read";
    public const string UsersRolesAssign = "users.roles.assign";
    public const string TenantsKeysMint = "tenants.keys.mint";
    public const string MoneyRead = "money.read";
    public const string MoneyOperate = "money.operate";
    public const string PlatformGenesis = "platform.genesis";
    public const string RecoveryOperate = "recovery.operate";
    public const string KeysRotate = "keys.rotate";
    public const string CatalogManage = "catalog.manage";

    /// <summary>Every permission, in catalog order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        AdminsRead, AdminsManage, RolesRead, RolesManage, AuditRead,
        GovernanceRead, GovernanceWrite, KycRead, KycDecide, KycConfigure,
        UsersRead, UsersRolesAssign, TenantsKeysMint, MoneyRead, MoneyOperate,
        PlatformGenesis, RecoveryOperate, KeysRotate, CatalogManage,
    ];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    /// <summary>Every <c>*.read</c> permission.</summary>
    public static readonly IReadOnlyList<string> Reads = All.Where(permission => permission.EndsWith(".read", StringComparison.Ordinal)).ToArray();

    /// <summary>Break-glass root holds people-management only (M3): never money, KYC, governance, platform or recovery.</summary>
    public static readonly IReadOnlySet<string> Root = new HashSet<string>(
        [AdminsRead, AdminsManage, RolesRead, RolesManage, AuditRead], StringComparer.Ordinal);

    public const string NodeOwnerRoleId = "node-owner";

    /// <summary>Immutable built-in roles seeded at boot.</summary>
    public static readonly IReadOnlyList<NodeAdminBuiltInRole> BuiltInRoles =
    [
        new(NodeOwnerRoleId, "node-owner", All),
        new("governance", "governance", Union([GovernanceRead, GovernanceWrite, AuditRead], Reads)),
        new("compliance", "compliance", [KycRead, KycDecide, KycConfigure, UsersRead, AuditRead]),
        new("operations", "operations", Union([MoneyRead, MoneyOperate, RecoveryOperate], Reads)),
        new("auditor", "auditor", Union([AuditRead], Reads)),
    ];

    public static bool IsKnown(string? permission) => permission is not null && Known.Contains(permission);

    public static bool IsBuiltInRoleId(string? roleId) => BuiltInRoles.Any(role => string.Equals(role.Id, roleId, StringComparison.Ordinal));

    /// <summary>Holds both people-management permissions (counts toward the last-owner invariant).</summary>
    public static bool IsOwnerCapable(IReadOnlySet<string> permissions)
        => permissions.Contains(AdminsManage) && permissions.Contains(RolesManage);

    /// <summary>Holds every permission in the catalog (node-owner equivalent).</summary>
    public static bool IsOwnerEquivalent(IReadOnlySet<string> permissions) => All.All(permissions.Contains);

    /// <summary>Catalog-ordered, de-duplicated copy of known permissions.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> permissions)
    {
        var set = new HashSet<string>(permissions, StringComparer.Ordinal);
        return All.Where(set.Contains).ToArray();
    }

    private static IReadOnlyList<string> Union(IEnumerable<string> first, IEnumerable<string> second)
        => Normalize(first.Concat(second));
}

/// <summary>A seeded, immutable node-admin role.</summary>
public sealed record NodeAdminBuiltInRole(string Id, string Name, IReadOnlyList<string> Permissions);

/// <summary>Stable <c>result.code</c> values of the node-admin wire contract.</summary>
public static class NodeAdminErrorCodes
{
    public const string AuthFailed = "AUTH_FAILED";
    public const string StepUpRequired = "STEP_UP_REQUIRED";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string DominanceRequired = "DOMINANCE_REQUIRED";
    public const string EscalationRefused = "ESCALATION_REFUSED";
    public const string LastOwner = "LAST_OWNER";
    public const string SelfActionRefused = "SELF_ACTION_REFUSED";
    public const string EnrollmentAlreadyStarted = "ENROLLMENT_ALREADY_STARTED";
    public const string EnrollmentInvalid = "ENROLLMENT_INVALID";
    public const string FingerprintMismatch = "FINGERPRINT_MISMATCH";
    public const string AdminLocked = "ADMIN_LOCKED";
    public const string UsernameTaken = "USERNAME_TAKEN";
    public const string RoleInUse = "ROLE_IN_USE";
    public const string RoleBuiltIn = "ROLE_BUILT_IN";
    public const string UnknownPermission = "UNKNOWN_PERMISSION";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string AuditUnavailable = "AUDIT_UNAVAILABLE";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string Unavailable = "NODE_ADMIN_UNAVAILABLE";
    public const string RateLimited = "RATE_LIMITED";

    /// <summary>Header naming the step-up requirement on a 403.</summary>
    public const string AuthRequirementHeader = "x-azoa-auth-requirement";

    public const string RecentNodeAdminLogin = "recent-node-admin-login";
}
