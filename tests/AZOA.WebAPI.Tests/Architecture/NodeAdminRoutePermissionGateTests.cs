using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Every operator-policy route has exactly one node-admin grant whose step-up flag mirrors the legacy policy (plan §4, M7);
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
    public void Grants_name_catalog_permissions_and_step_up_mirrors_the_recent_operator_policies()
    {
        var problems = new List<string>();
        foreach (var (key, policies) in OperatorRoutes())
        {
            if (!NodeAdminRoutePermissions.Map.TryGetValue(key, out var grant)) continue;
            if (grant.Permission is not null && !NodeAdminPermissions.IsKnown(grant.Permission))
                problems.Add($"{key}: unknown permission '{grant.Permission}'");
            var recent = policies.Overlaps(NodeAdminRoutePermissions.RecentOperatorPolicies);
            if (grant.RequireRecent != recent)
                problems.Add($"{key}: RequireRecent={grant.RequireRecent} but the route's policies [{string.Join(", ", policies)}] say {recent}");
        }
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
