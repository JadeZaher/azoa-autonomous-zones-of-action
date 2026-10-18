using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Every operator-policy route has exactly one node-admin grant whose step-up is never weaker than the legacy policy (plan §4, M7, D-M1);
/// see Services/NodeAdmin/AGENTS.md §route-permission-map.</summary>
public sealed class NodeAdminRoutePermissionGateTests
{
    [Fact]
    public void Every_live_operator_route_is_mapped_and_no_mapping_is_stale()
    {
        var operatorRoutes = OperatorRoutes();
        operatorRoutes.Should().NotBeEmpty();

        var unmapped = operatorRoutes.Keys.Where(key => !NodeAdminRoutePermissions.Map.ContainsKey(key)).Order(StringComparer.Ordinal).ToArray();
        string.Join(Environment.NewLine, unmapped).Should().BeEmpty(
            "a route carrying Operator/NodeGovern/NodeOperatorSession/RecentNodeOperatorSession/DappRoleAssignment needs a grant in NodeAdminRoutePermissions.Map (node admins are denied on it otherwise)");

        var stale = NodeAdminRoutePermissions.Map.Keys.Where(key => !operatorRoutes.ContainsKey(key)).Order(StringComparer.Ordinal).ToArray();
        string.Join(Environment.NewLine, stale).Should().BeEmpty("every grant must name a live operator-policy route");
    }

    [Fact]
    public void Grants_name_catalog_permissions_and_node_admin_step_up_is_never_weaker_than_legacy()
    {
        var problems = new List<string>();
        foreach (var (key, policies) in OperatorRoutes())
        {
            if (!NodeAdminRoutePermissions.Map.TryGetValue(key, out var grant)) continue;
            if (grant.Permission is not null && !NodeAdminPermissions.IsKnown(grant.Permission))
                problems.Add($"{key}: unknown permission '{grant.Permission}'");
            // D-M1: node-admin step-up ⊇ legacy step-up; stricter than legacy is allowed.
            if (policies.Overlaps(NodeAdminRoutePermissions.RecentOperatorPolicies) && !grant.RequireRecent)
                problems.Add($"{key}: the legacy policy [{string.Join(", ", policies)}] requires a recent login but the node-admin grant does not");
        }
        string.Join(Environment.NewLine, problems).Should().BeEmpty();
    }

    [Fact]
    public void Every_node_admin_write_requires_step_up_and_never_maps_to_a_read_permission()
    {
        var problems = new List<string>();
        foreach (var (key, grant) in NodeAdminRoutePermissions.Map)
        {
            if (key.StartsWith("GET ", StringComparison.Ordinal) || grant.Permission is null) continue;
            var readOnly = NodeAdminRoutePermissions.ReadOnlyWriteVerbRoutes.Contains(key);
            var readPermission = grant.Permission.EndsWith(".read", StringComparison.Ordinal);
            // L-1: a write verb on a *.read grant is only legal for an allow-listed body-carried query.
            if (readPermission && !readOnly)
                problems.Add($"{key}: a write verb maps to '{grant.Permission}'; map the domain permission or add it to ReadOnlyWriteVerbRoutes with a reason");
            if (readOnly && !readPermission)
                problems.Add($"{key}: allow-listed as read-only but maps to the write permission '{grant.Permission}'");
            // D-M1: every node-admin write (money, recovery, genesis, keys, catalog, governance, KYC, roles, tenant keys) is step-up.
            if (!readOnly && !grant.RequireRecent)
                problems.Add($"{key}: node-admin writes require step-up (auth_time <= 10 min)");
        }
        NodeAdminRoutePermissions.ReadOnlyWriteVerbRoutes.Should().OnlyContain(key => NodeAdminRoutePermissions.Map.ContainsKey(key));
        string.Join(Environment.NewLine, problems).Should().BeEmpty();
    }

    [Fact]
    public void Every_inventoried_operator_route_row_is_mapped()
    {
        var rows = GateInventory.Load("RouteAuthorization")
            .Where(row => row.Strings("policies")?.Any(NodeAdminRoutePermissions.OperatorPolicies.Contains) == true)
            .Select(row => GateInventory.SplitRoute(row.Text("route")))
            .Select(route => NodeAdminRoutePermissions.Key(route.Verb!, route.Template))
            .ToArray();
        rows.Should().NotBeEmpty();
        rows.Where(key => !NodeAdminRoutePermissions.Map.ContainsKey(key)).Should().BeEmpty(
            "every RouteAuthorization inventory row with an operator policy must have a node-admin grant");
    }

    /// <summary>Live controller routes carrying an operator policy, keyed like the production map.</summary>
    private static Dictionary<string, HashSet<string>> OperatorRoutes()
        => GateInventory.Routes()
            .Select(route => (Key: NodeAdminRoutePermissions.Key(route.Verb, route.Template),
                Policies: GateInventory.Policies(route.Method).ToHashSet(StringComparer.Ordinal)))
            .Where(route => route.Policies.Overlaps(NodeAdminRoutePermissions.OperatorPolicies))
            .GroupBy(route => route.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.SelectMany(route => route.Policies).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
}
