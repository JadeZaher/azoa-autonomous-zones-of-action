using System.Reflection;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;

namespace AZOA.WebAPI.Tests.NodeAdmin;

/// <summary>Pure RBAC rules: break-glass scope (M3), policy names fail closed, last-owner counting over live roles.</summary>
public sealed class NodeAdminRbacTests
{
    [Fact]
    public void Root_sessions_never_hold_money_kyc_governance_platform_or_recovery_permissions()
    {
        NodeAdminPermissions.Root.Should().OnlyContain(permission =>
            permission.StartsWith("admins.", StringComparison.Ordinal) || permission.StartsWith("roles.", StringComparison.Ordinal)
            || permission == NodeAdminPermissions.AuditRead);
        NodeAdminPermissions.IsOwnerCapable(NodeAdminPermissions.Root).Should().BeTrue("root must be able to bootstrap and recover owners");
    }

    [Fact]
    public void Built_in_roles_grant_only_catalog_permissions_and_only_node_owner_can_manage_people()
    {
        NodeAdminPermissions.BuiltInRoles.SelectMany(role => role.Permissions).Should().OnlyContain(permission => NodeAdminPermissions.IsKnown(permission));
        NodeAdminPermissions.BuiltInRoles
            .Where(role => NodeAdminPermissions.IsOwnerCapable(role.Permissions.ToHashSet(StringComparer.Ordinal)))
            .Select(role => role.Id).Should().Equal(NodeAdminPermissions.NodeOwnerRoleId);
        NodeAdminPermissions.BuiltInRoles.Single(role => role.Id == "auditor").Permissions
            .Should().OnlyContain(permission => permission.EndsWith(".read", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("NodeAdmin:session", null, false, true)]
    [InlineData("NodeAdmin:session:recent", null, true, true)]
    [InlineData("NodeAdmin:admins.manage:recent", "admins.manage", true, true)]
    [InlineData("NodeAdmin:money.operate", "money.operate", false, true)]
    [InlineData("NodeAdmin:money.print", null, false, false)]
    [InlineData("NodeAdmin:admins.manage:stale", null, false, false)]
    [InlineData("Operator", null, false, false)]
    public void Policy_names_parse_only_for_catalog_permissions(string name, string? permission, bool recent, bool valid)
    {
        NodeAdminPolicies.TryParse(name, out var parsedPermission, out var parsedRecent).Should().Be(valid);
        if (!valid) return;
        parsedPermission.Should().Be(permission);
        parsedRecent.Should().Be(recent);
    }

    [Fact]
    public void Every_node_admin_policy_on_a_controller_resolves()
    {
        var policies = typeof(Program).Assembly.GetTypes()
            .Where(type => type.Name.StartsWith("NodeAdmin", StringComparison.Ordinal) && type.Name.EndsWith("Controller", StringComparison.Ordinal))
            .SelectMany(type => type.GetCustomAttributes<AuthorizeAttribute>()
                .Concat(type.GetMethods().SelectMany(method => method.GetCustomAttributes<AuthorizeAttribute>())))
            .Select(attribute => attribute.Policy)
            .Distinct().ToArray();

        policies.Should().NotBeEmpty();
        policies.Where(policy => !NodeAdminPolicies.TryParse(policy, out _, out _)).Should().BeEmpty(
            "an unresolvable policy name would 500 every request instead of denying");
    }

    [Fact]
    public void Owner_count_uses_live_role_permissions_and_ignores_non_active_admins()
    {
        var roles = Snapshot(("owner", NodeAdminPermissions.All), ("people", [NodeAdminPermissions.AdminsManage, NodeAdminPermissions.RolesManage]),
            ("half", [NodeAdminPermissions.AdminsManage]));
        var admins = new[]
        {
            Admin("a", NodeAdminAccount.StatusActive, "owner"),
            Admin("b", NodeAdminAccount.StatusActive, "half"),
            Admin("c", NodeAdminAccount.StatusSuspended, "owner"),
            Admin("d", NodeAdminAccount.StatusActive, "people"),
        };

        NodeAdminInvariants.OwnerCount(admins, roles).Should().Be(2);
        var stripped = NodeAdminInvariants.WithRole(roles, "people", [NodeAdminPermissions.AdminsRead]);
        NodeAdminInvariants.OwnerCount(admins, stripped).Should().Be(1, "editing a role changes every holder at once");
        var suspended = NodeAdminInvariants.With(admins, NodeAdminInvariants.Proposed(admins[0], status: NodeAdminAccount.StatusSuspended));
        NodeAdminInvariants.BreaksLastOwner(NodeAdminInvariants.OwnerCount(admins, stripped),
            NodeAdminInvariants.OwnerCount(suspended, stripped)).Should().BeTrue();
        NodeAdminInvariants.BreaksLastOwner(0, 0).Should().BeFalse("a node with no owner yet (bootstrap) is not blocked");
    }

    [Theory]
    [InlineData(new[] { "admins.read", "admins.manage" }, new[] { "admins.read" }, true)]
    [InlineData(new[] { "admins.read" }, new[] { "admins.read", "money.read" }, false)]
    [InlineData(new[] { "admins.read" }, new string[0], true)]
    public void Dominance_requires_every_target_permission(string[] actor, string[] target, bool dominates)
    {
        NodeAdminInvariants.Dominates(actor.ToHashSet(), target.ToHashSet()).Should().Be(dominates);
        NodeAdminInvariants.Escalates(actor.ToHashSet(), target).Should().Be(!dominates);
    }

    private static NodeAdminRoleSnapshot Snapshot(params (string Id, IReadOnlyList<string> Permissions)[] roles)
        => new(1, roles.ToDictionary(role => role.Id, role => new NodeAdminRole { Id = role.Id, Name = role.Id, Permissions = role.Permissions },
            StringComparer.Ordinal));

    private static NodeAdminAccount Admin(string id, string status, params string[] roleIds)
        => new() { Id = id, Username = id, DisplayName = id, Status = status, RoleIds = roleIds, CreatedBy = "root:test" };
}
