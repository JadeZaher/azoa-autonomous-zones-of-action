using System.Text.RegularExpressions;
using AZOA.WebAPI.Services.Consent;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Every mutating route is classified by exactly one lane inventory row whose declared authorization equals the action's
/// real <c>[Authorize]</c> metadata, and the child-credential allowlist equals the inventory's; see Architecture/AGENTS.md §Gate inventories.</summary>
public sealed partial class RouteAuthorizationGateTests
{
    private static readonly string[] MutatingVerbs = ["POST", "PUT", "DELETE", "PATCH"];
    private static readonly string[] Classes = ["value-moving", "non-value"];

    [Fact]
    public void Every_controller_action_declares_its_http_verbs()
    {
        var verbless = GateInventory.Actions()
            .Where(item => !item.Method.GetCustomAttributes(true).OfType<Microsoft.AspNetCore.Mvc.Routing.IActionHttpMethodProvider>().Any())
            .Select(item => $"{item.Controller.Name}.{item.Method.Name}")
            .Order(StringComparer.Ordinal).ToArray();

        verbless.Should().BeEmpty("a [Route]-only action answers every verb and would bypass the mutating-route inventory; add [HttpX] or [NonAction]");
    }

    [Fact]
    public void Mutating_endpoints_are_controller_actions()
    {
        var root = GateInventory.RepoRoot();
        var excluded = new[] { "tests", "bin", "obj", "node_modules", "frontend", "sdk", ".git" };
        var minimal = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => excluded.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(item => MinimalMutationRegex().IsMatch(item.line))
            .Select(item => $"{Path.GetRelativePath(root, item.path)}:{item.index + 1}")
            .ToArray();

        minimal.Should().BeEmpty("a minimal-API mutation is invisible to the controller inventory; expose it as a controller action");
    }

    [Fact]
    public void Every_mutating_route_is_classified_by_a_lane_inventory()
    {
        var classified = Rows().Select(Key).ToHashSet(StringComparer.Ordinal);

        var unclassified = GateInventory.Routes()
            .Where(route => MutatingVerbs.Contains(route.Verb, StringComparer.OrdinalIgnoreCase))
            .Where(route => !classified.Contains(route.Key))
            .Select(route => route.Key)
            .Distinct().Order(StringComparer.Ordinal).ToArray();

        string.Join(Environment.NewLine, unclassified).Should().BeEmpty(
            "every POST/PUT/DELETE/PATCH route (verb + template + action) needs a row in its owning lane's Inventories/RouteAuthorization/<lane>.json");
    }

    [Fact]
    public void Route_rows_are_well_formed_unique_and_match_the_live_authorization()
    {
        var routes = GateInventory.Routes().GroupBy(route => route.Key).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var knownPolicies = GateInventory.Actions().SelectMany(item => GateInventory.Policies(item.Method))
            .Where(policy => policy is not ("authenticated" or "anonymous" or "none")).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var row in Rows())
        {
            var key = Key(row);
            var label = $"{row.File}: {key}";
            if (GateInventory.SplitRoute(row.Text("route")).Verb is null) problems.Add($"{label}: route must be \"VERB /template\"");
            if (!Classes.Contains(row.Text("class"))) problems.Add($"{label}: class must be value-moving or non-value");
            if (string.IsNullOrWhiteSpace(row.Text("enforcement"))) problems.Add($"{label}: enforcement is required");
            if (row.Text("class") == "value-moving" && string.IsNullOrWhiteSpace(row.Text("scope")))
                problems.Add($"{label}: a value-moving route must name its signing scope");
            if (!routes.TryGetValue(key, out var live))
            {
                problems.Add($"{label}: no such verb + route + action (stale row)");
                continue;
            }
            var actual = GateInventory.Policies(live.Method);
            var declared = row.Strings("policies")?.Order(StringComparer.Ordinal).ToArray();
            if (declared is null || !declared.SequenceEqual(actual, StringComparer.Ordinal))
                problems.Add($"{label}: declares policies [{string.Join(", ", declared ?? [])}] but the action carries [{string.Join(", ", actual)}]");
            var claimed = knownPolicies.Where(policy => Regex.IsMatch(row.Text("enforcement") ?? "", $@"\b{policy}\b")).ToArray();
            foreach (var policy in claimed.Where(policy => !actual.Contains(policy)))
                problems.Add($"{label}: enforcement names the {policy} policy, which the action does not carry");
        }

        problems.AddRange(Rows().GroupBy(Key).Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} is classified {group.Count()} times ({string.Join(", ", group.Select(row => row.File).Distinct())})"));

        string.Join(Environment.NewLine, problems).Should().BeEmpty();
    }

    [Fact]
    public void Child_credential_allowlist_equals_the_inventory_allowlist()
    {
        var declared = DelegatedChildCredentialCatalog.Endpoints()
            .Select(endpoint => Describe(ActionKey(endpoint.Controller.Name, endpoint.Action.Name), endpoint.Policy.Scope, endpoint.Policy.SigningScopes))
            .Order(StringComparer.Ordinal).ToArray();
        var inventoried = Rows().Where(row => !string.IsNullOrWhiteSpace(row.Text("childCredentialScope")))
            .Select(row => Describe(ActionKey(row.Text("controller"), row.Text("action")), row.Text("childCredentialScope")!, SigningScopes(row)))
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

    private static IEnumerable<string> SigningScopes(GateInventory.Row row) => row.Strings("childSigningScopes") ?? [];

    private static IReadOnlyList<GateInventory.Row> Rows() => GateInventory.Load("RouteAuthorization");

    private static string Key(GateInventory.Row row)
    {
        var (verb, template) = GateInventory.SplitRoute(row.Text("route"));
        return GateInventory.RouteKey(verb, template, row.Text("controller"), row.Text("action"));
    }

    private static string ActionKey(string? controller, string? action) => $"{GateInventory.ControllerName(controller)}.{action}";

    [GeneratedRegex(@"\.Map(Post|Put|Delete|Patch|Methods)\s*\(")]
    private static partial Regex MinimalMutationRegex();
}
