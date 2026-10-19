using AZOA.WebAPI.Core;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Role snapshot at one directory revision.</summary>
public sealed record NodeAdminRoleSnapshot(long DirectoryRevision, IReadOnlyDictionary<string, NodeAdminRole> Roles)
{
    /// <summary>Union of the known permissions of <paramref name="roleIds"/>; unknown role ids grant nothing.</summary>
    public IReadOnlySet<string> PermissionsOf(IEnumerable<string> roleIds)
    {
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var roleId in roleIds)
            if (Roles.TryGetValue(roleId, out var role))
                permissions.UnionWith(role.Permissions.Where(NodeAdminPermissions.IsKnown));
        return permissions;
    }

    /// <summary>Effective permissions of an admin: empty unless the admin is active.</summary>
    public IReadOnlySet<string> EffectivePermissions(NodeAdminAccount admin)
        => admin.Status == NodeAdminAccount.StatusActive ? PermissionsOf(admin.RoleIds) : new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>Computes live permissions per request (C2); roles are cached per directory revision, which every role change bumps.</summary>
public sealed class NodeAdminPermissionResolver
{
    private volatile NodeAdminRoleSnapshot? _cached;

    public async Task<NodeAdminRoleSnapshot> SnapshotAsync(SurrealNodeAdminStore store, CancellationToken ct)
    {
        var directory = await store.GetDirectoryAsync(ct);
        var revision = directory?.Revision ?? -1;
        var cached = _cached;
        if (cached is not null && cached.DirectoryRevision == revision && revision >= 0) return cached;
        var roles = await store.ListRolesAsync(ct);
        var snapshot = new NodeAdminRoleSnapshot(revision, roles.ToDictionary(role => role.Id, StringComparer.Ordinal));
        _cached = snapshot;
        return snapshot;
    }
}
