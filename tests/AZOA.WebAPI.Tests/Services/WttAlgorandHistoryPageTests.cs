using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttAlgorandHistoryPageTests
{
    [Fact]
    public void Pages_retain_mutations_and_target_only_and_deduplicate_the_same_inner_root_at_boundary()
    {
        var create = Root("create", 1, 0, 0, "noop", 7);
        var call = Root("ordinary", 20, 4, 7, "noop");
        var target = Root("target", 50, 10, 7, "noop");
        var state = WttAlgorandHistoryPage.Advance(7, "target", 50, 10, Empty(), Page(create, call));
        Assert.Single(state.RelevantRoots);
        state = WttAlgorandHistoryPage.Advance(7, "target", 50, 10, state, Page(call, target));
        Assert.Equal(new[] { "create", "target" }, state.RelevantRoots.Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(50UL, state.LastRound);
        Assert.Equal("target", state.LastRootId);
        var after = Root("after", 50, 11, 7, "update");
        state = WttAlgorandHistoryPage.Advance(7, "target", 50, 10, state, Page(after));
        Assert.Equal(2, state.RelevantRoots.Count);
        Assert.Equal("after", state.LastRootId);
    }

    [Fact]
    public void Root_containing_nested_mutation_is_retained_even_when_outer_application_differs()
    {
        var child = Root("inner", 12, 3, 7, "update");
        var outer = Root("outer", 12, 3, 99, "noop", children: [child]);
        var state = WttAlgorandHistoryPage.Advance(7, "target", 50, 10, Empty(), Page(outer));
        Assert.Single(state.RelevantRoots);
        Assert.Equal("outer", state.RelevantRoots[0].GetProperty("id").GetString());
    }

    [Fact]
    public void Changed_duplicate_out_of_order_or_future_records_cannot_advance_the_checkpoint()
    {
        var prior = Root("prior", 20, 4, 7, "noop");
        var state = WttAlgorandHistoryPage.Advance(7, "target", 50, 10, Empty(), Page(prior));
        Assert.Throws<InvalidDataException>(() => WttAlgorandHistoryPage.Advance(7, "target", 50, 10, state,
            Page(Root("prior", 20, 4, 7, "update"))));
        Assert.Throws<InvalidDataException>(() => WttAlgorandHistoryPage.Advance(7, "target", 50, 10, state,
            Page(Root("other", 20, 4, 7, "noop"))));
        Assert.Throws<InvalidDataException>(() => WttAlgorandHistoryPage.Advance(7, "target", 50, 10, state,
            Page(Root("back", 19, 100, 7, "noop"))));
        Assert.Throws<InvalidDataException>(() => WttAlgorandHistoryPage.Advance(7, "target", 50, 10, state,
            Page(Root("future", 51, 0, 7, "noop"))));
        Assert.Equal("prior", state.LastRootId);
    }

    private static WttAlgorandHistoryPage.State Empty() => new(1, [], 0, 0, null, null);
    private static JsonElement Page(params JsonElement[] roots) => JsonSerializer.SerializeToElement(roots);
    private static JsonElement Root(string id, ulong round, ulong offset, ulong application, string completion,
        ulong? created = null, IReadOnlyList<JsonElement>? children = null)
    {
        var value = new Dictionary<string, object> { ["id"] = id, ["confirmed-round"] = round,
            ["intra-round-offset"] = offset, ["application-transaction"] = new Dictionary<string, object>
                { ["application-id"] = application, ["on-completion"] = completion } };
        if (created is not null) value["created-application-index"] = created.Value;
        if (children is not null) value["inner-txns"] = children;
        return JsonSerializer.SerializeToElement(value);
    }
}
