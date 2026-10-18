using AZOA.WebAPI.Core;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Pure RBAC invariants (C1 dominance, no escalation, last owner); see <c>Services/NodeAdmin/AGENTS.md</c> §invariants.</summary>
public static class NodeAdminInvariants
{
    /// <summary>True when every permission of <paramref name="target"/> is held by <paramref name="actor"/>.</summary>
    public static bool Dominates(IReadOnlySet<string> actor, IReadOnlySet<string> target) => target.All(actor.Contains);

    /// <summary>True when granting <paramref name="granted"/> would give someone a permission the actor does not hold.</summary>
    public static bool Escalates(IReadOnlySet<string> actor, IEnumerable<string> granted) => granted.Any(permission => !actor.Contains(permission));

    /// <summary>Number of active admins holding both admins.manage and roles.manage.</summary>
    public static int OwnerCount(IEnumerable<NodeAdminAccount> admins, NodeAdminRoleSnapshot roles)
        => admins.Count(admin => NodeAdminPermissions.IsOwnerCapable(roles.EffectivePermissions(admin)));

    /// <summary>LAST_OWNER: refuse a change that takes the owner count from at least one to zero.</summary>
    public static bool BreaksLastOwner(int ownersBefore, int ownersAfter) => ownersBefore > 0 && ownersAfter == 0;

    /// <summary>The admins list with one admin replaced by its proposed state.</summary>
    public static IReadOnlyList<NodeAdminAccount> With(IEnumerable<NodeAdminAccount> admins, NodeAdminAccount proposed)
        => admins.Select(admin => admin.Id == proposed.Id ? proposed : admin).ToArray();

    /// <summary>A shallow copy of an admin with a different status and/or role set.</summary>
    public static NodeAdminAccount Proposed(NodeAdminAccount admin, string? status = null, IReadOnlyList<string>? roleIds = null) => new()
    {
        Id = admin.Id,
        Username = admin.Username,
        DisplayName = admin.DisplayName,
        Status = status ?? admin.Status,
        RoleIds = roleIds ?? admin.RoleIds,
        AdminRevision = admin.AdminRevision,
        SessionRevision = admin.SessionRevision,
        TotpSecretProtected = admin.TotpSecretProtected,
        TotpLastStep = admin.TotpLastStep,
        CreatedBy = admin.CreatedBy,
        CreatedAt = admin.CreatedAt,
        UpdatedAt = admin.UpdatedAt,
    };

    /// <summary>The role snapshot with one role's permissions replaced (or removed when null).</summary>
    public static NodeAdminRoleSnapshot WithRole(NodeAdminRoleSnapshot roles, string roleId, IReadOnlyList<string>? permissions)
    {
        var copy = roles.Roles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (permissions is null)
            copy.Remove(roleId);
        else if (copy.TryGetValue(roleId, out var role))
            copy[roleId] = new NodeAdminRole { Id = role.Id, Name = role.Name, BuiltIn = role.BuiltIn, Permissions = permissions, Revision = role.Revision };
        return roles with { Roles = copy };
    }
}
