using AZOA.WebAPI.Services.Consent;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Every mutating route is classified by exactly one lane inventory, and the child-credential allowlist equals the inventory's.</summary>
public sealed class RouteAuthorizationGateTests
{
    private static readonly string[] MutatingVerbs = ["POST", "PUT", "DELETE", "PATCH"];
    private static readonly string[] Classes = ["value-moving", "non-value"];

    [Fact]
    public void Every_mutating_route_is_classified_by_a_lane_inventory()
    {
        var classified = Rows().Select(row => Key(row.Text("controller"), row.Text("action"))).ToHashSet(StringComparer.Ordinal);

        var unclassified = GateInventory.Routes()
            .Where(route => MutatingVerbs.Contains(route.Verb, StringComparer.OrdinalIgnoreCase))
            .Where(route => !classified.Contains(Key(route.Controller, route.Action)))
            .Select(route => $"{route.Verb} /{route.Template} ({route.Controller}.{route.Action})")
            .Distinct().Order(StringComparer.Ordinal).ToArray();

        string.Join(Environment.NewLine, unclassified).Should().BeEmpty("every POST/PUT/DELETE/PATCH route needs a row in its owning lane's Inventories/RouteAuthorization/<lane>.json");
    }

    [Fact]
    public void Route_rows_are_well_formed_unique_and_point_at_live_actions()
    {
        var routes = GateInventory.Routes().GroupBy(route => Key(route.Controller, route.Action))
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var row in Rows())
        {
            var key = Key(row.Text("controller"), row.Text("action"));
            var label = $"{row.File}: {key}";
            if (!Classes.Contains(row.Text("class"))) problems.Add($"{label}: class must be value-moving or non-value");
            if (string.IsNullOrWhiteSpace(row.Text("enforcement"))) problems.Add($"{label}: enforcement is required");
            if (row.Text("class") == "value-moving" && string.IsNullOrWhiteSpace(row.Text("scope")))
                problems.Add($"{label}: a value-moving route must name its signing scope");
            if (!routes.TryGetValue(key, out var live))
            {
                problems.Add($"{label}: no such controller action (stale row)");
                continue;
            }
            var route = GateInventory.NormalizeRoute(row.Text("route"));
            if (route.Length > 0 && live.All(item => item.Template != route))
                problems.Add($"{label}: route '{row.Text("route")}' does not match /{live[0].Template}");
        }

        problems.AddRange(Rows().GroupBy(row => Key(row.Text("controller"), row.Text("action")))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} is classified {group.Count()} times ({string.Join(", ", group.Select(row => row.File).Distinct())})"));

        string.Join(Environment.NewLine, problems).Should().BeEmpty();
    }

    [Fact]
    public void Child_credential_allowlist_equals_the_inventory_allowlist()
    {
        var declared = DelegatedChildCredentialCatalog.Endpoints()
            .Select(endpoint => Describe(Key(endpoint.Controller.Name, endpoint.Action.Name), endpoint.Policy.Scope, endpoint.Policy.SigningScopes))
            .Order(StringComparer.Ordinal).ToArray();
        var inventoried = Rows().Where(row => !string.IsNullOrWhiteSpace(row.Text("childCredentialScope")))
            .Select(row => Describe(Key(row.Text("controller"), row.Text("action")), row.Text("childCredentialScope")!, SigningScopes(row)))
            .Order(StringComparer.Ordinal).ToArray();

        declared.Should().Equal(inventoried,
            "an endpoint accepts a tenant child JWT only when its [DelegatedChildCredential] scopes are recorded in its lane inventory");
        DelegatedChildCredentialCatalog.Scopes.Should().BeEquivalentTo(Rows()
            .Where(row => !string.IsNullOrWhiteSpace(row.Text("childCredentialScope")))
            .SelectMany(row => SigningScopes(row).Prepend(row.Text("childCredentialScope")!)).Distinct(),
            "the delegable scope vocabulary is exactly what the inventoried allowlist declares");
    }

    private static string Describe(string key, string scope, IEnumerable<string> signing)
        => $"{key} -> {scope} [{string.Join(",", signing.Order(StringComparer.Ordinal))}]";

    private static IEnumerable<string> SigningScopes(GateInventory.Row row)
        => row.Value.TryGetProperty("childSigningScopes", out var scopes) && scopes.ValueKind == System.Text.Json.JsonValueKind.Array
            ? scopes.EnumerateArray().Select(scope => scope.GetString()!).ToArray() : [];

    private static IReadOnlyList<GateInventory.Row> Rows() => GateInventory.Load("RouteAuthorization");

    private static string Key(string? controller, string? action) => $"{GateInventory.ControllerName(controller)}.{action}";
}
