using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttAlgorandProgramHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execution_position_distinguishes_updates_before_and_after_the_exact_inner_call(bool updateBefore)
    {
        var call = App(7, "noop"); var update = App(7, "update", 2);
        var children = updateBefore ? new[] { update, call } : new[] { call, update };
        var root = Root("target", 50, 10, App(9, "noop", children: children));
        var path = updateBefore ? "1" : "0";
        var history = new[] { Create(), root };
        Assert.Equal(!updateBefore, WttAlgorandProgramHistory.Matches(history, 7, "target", path, 50, 10, Hash(1), Hash(1)));
        Assert.Equal(updateBefore, WttAlgorandProgramHistory.Matches(history, 7, "target", path, 50, 10, Hash(2), Hash(2)));
    }

    [Fact]
    public void Repeated_root_matches_from_inner_query_are_deduplicated_but_changed_content_is_refused()
    {
        var root = Root("target", 50, 10, App(9, "noop", children: [App(7, "noop")]));
        Assert.True(WttAlgorandProgramHistory.Matches([root, Create(), root], 7, "target", "0", 50, 10, Hash(1), Hash(1)));
        var changed = Root("target", 50, 10, App(9, "noop", children: [App(7, "update", 2)]));
        Assert.False(WttAlgorandProgramHistory.Matches([Create(), root, changed], 7, "target", "0", 50, 10, Hash(1), Hash(1)));
    }

    [Fact]
    public void Nested_creation_and_updates_follow_execution_tree_order_not_repeated_inner_offsets()
    {
        var creation = App(0, "noop", 1); creation["created-application-index"] = 7;
        var createRoot = Root("create-root", 1, 0, App(9, "noop", children: [creation]));
        var updateRoot = Root("update-root", 40, 5, App(9, "noop", children: [App(7, "update", 2)]));
        var target = Root("target", 50, 10, App(9, "noop", children: [App(8, "noop", children: [App(7, "noop")])]));
        Assert.True(WttAlgorandProgramHistory.Matches([target, createRoot, updateRoot], 7, "target", "0/0", 50, 10, Hash(2), Hash(2)));
        Assert.False(WttAlgorandProgramHistory.Matches([target, updateRoot], 7, "target", "0/0", 50, 10, Hash(2), Hash(2)));
        Assert.False(WttAlgorandProgramHistory.Matches([target, createRoot, updateRoot], 7, "target", "0", 50, 10, Hash(2), Hash(2)));
    }

    private static JsonElement Create()
    {
        var app = App(0, "noop", 1); app["created-application-index"] = 7;
        return Root("create", 1, 0, app);
    }

    private static JsonElement Root(string id, ulong round, ulong offset, Dictionary<string, object> transaction)
    {
        transaction["id"] = id; transaction["confirmed-round"] = round; transaction["intra-round-offset"] = offset;
        return JsonSerializer.SerializeToElement(transaction);
    }

    private static Dictionary<string, object> App(ulong id, string completion, byte? program = null,
        IReadOnlyList<Dictionary<string, object>>? children = null)
    {
        var app = new Dictionary<string, object> { ["application-id"] = id, ["on-completion"] = completion };
        if (program is { } value)
        {
            app["approval-program"] = Convert.ToBase64String(new[] { value });
            app["clear-state-program"] = Convert.ToBase64String(new[] { value });
        }
        var transaction = new Dictionary<string, object> { ["application-transaction"] = app, ["intra-round-offset"] = 10 };
        if (children is not null) transaction["inner-txns"] = children;
        return transaction;
    }

    private static string Hash(byte value) => Convert.ToHexStringLower(SHA256.HashData(new[] { value }));
}
