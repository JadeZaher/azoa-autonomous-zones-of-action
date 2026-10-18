using FluentAssertions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Every owned-resource store method (any public <c>*Store</c>/<c>*Ledger</c> contract) states how it keeps one tenant's or
/// user's rows from another; see Architecture/AGENTS.md §Gate inventories.</summary>
public sealed class CrossTenantGateTests
{
    [Fact]
    public void Every_owned_resource_store_method_is_classified_by_a_lane_inventory()
    {
        var classified = Rows().Select(row => Key(row.Text("store"), row.Text("method"))).ToHashSet(StringComparer.Ordinal);

        var unclassified = GateInventory.OwnedStoreMethods()
            .Where(surface => !classified.Contains(Key(surface.Store, surface.Method)))
            .Select(surface => Key(surface.Store, surface.Method))
            .Order(StringComparer.Ordinal).ToArray();

        string.Join(Environment.NewLine, unclassified).Should().BeEmpty("every owned-resource store method needs a tenantCheck row in its owning lane's Inventories/CrossTenant/<lane>.json");
    }

    [Fact]
    public void Store_rows_are_well_formed_unique_and_point_at_live_methods()
    {
        var live = GateInventory.StoreContracts()
            .SelectMany(type => type.GetMethods().Select(method => Key(type.Name, method.Name)))
            .ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var row in Rows())
        {
            var key = Key(row.Text("store"), row.Text("method"));
            if (string.IsNullOrWhiteSpace(row.Text("tenantCheck"))) problems.Add($"{row.File}: {key}: tenantCheck is required");
            if (!live.Contains(key)) problems.Add($"{row.File}: {key}: no such store contract method (stale row; key by the interface name and one method per row)");
        }

        problems.AddRange(Rows().GroupBy(row => Key(row.Text("store"), row.Text("method")))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} is classified {group.Count()} times ({string.Join(", ", group.Select(row => row.File).Distinct())})"));

        string.Join(Environment.NewLine, problems).Should().BeEmpty();
    }

    private static IReadOnlyList<GateInventory.Row> Rows() => GateInventory.Load("CrossTenant");

    private static string Key(string? store, string? method) => $"{store}.{method}";
}
