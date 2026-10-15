using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using Xunit;
using ActionModel = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// Unit tests for <see cref="ActionConfigBindingResolver"/>: binding resolution
/// (upstream + holon), fail-closed paths, structural violations, and shadow
/// round-trip acceptance (AC-1a..1f).
/// </summary>
public class ActionConfigBindingResolverTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid NodeId   = Guid.NewGuid();
    private static readonly Guid ActionId  = Guid.NewGuid();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ActionNode MakeNode(string name, string? config = null) =>
        new() { Id = NodeId, ActionId = ActionId, Name = name, Config = config ?? "{}" };

    private static ActionNode MakeNodeWithId(Guid id, string name, string? config = null) =>
        new() { Id = id, ActionId = ActionId, Name = name, Config = config ?? "{}" };

    private static ActionNodeExecution MakeExec(Guid nodeId, string output) =>
        new()
        {
            Id = Guid.NewGuid(), RunId = Guid.NewGuid(), NodeId = nodeId,
            State = ActionNodeState.Succeeded, Output = output
        };

    private static IHolon MakeHolon(Guid userId, Dictionary<string, string>? metadata = null) =>
        new TestHolon
        {
            Id = Guid.NewGuid(), Name = "TestHolon", UserId = userId,
            Metadata = metadata ?? [], IsActive = true,
            PeerHolonIds = [], CreatedDate = DateTime.UtcNow
        };

    private static IHolonManager MakeHolonManager(IHolon? holon = null, Guid? holonId = null)
    {
        var mock = new Mock<IHolonManager>();
        // Owner-or-public read scope threads a callerUserId now; the resolver's own
        // UserId post-check does the owner-strict filtering, so the mock matches any
        // caller id and returns the seeded holon verbatim.
        if (holon is not null && holonId is not null)
        {
            mock.Setup(m => m.GetAsync(holonId.Value, It.IsAny<Guid?>(), It.IsAny<AZOA.WebAPI.Models.Requests.AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IHolon> { Result = holon });
        }
        else
        {
            mock.Setup(m => m.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOA.WebAPI.Models.Requests.AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IHolon> { IsError = true, Message = "not found" });
        }
        return mock.Object;
    }

    private static ActionModel MakeActionWithEdge(ActionNode source, ActionNode target) =>
        new()
        {
            Id = ActionId, UserId = UserId,
            Nodes = [source, target],
            Edges =
            [
                new ActionEdge
                {
                    Id = Guid.NewGuid(), ActionId = ActionId,
                    SourceNodeId = source.Id, TargetNodeId = target.Id,
                    EdgeType = ActionEdgeType.Control
                }
            ]
        };

    private static ActionConfigBindingResolver MakeResolver(IHolonManager? holonManager = null) =>
        new(holonManager ?? MakeHolonManager());

    // ── No-binding passthrough ────────────────────────────────────────────────

    [Fact]
    public async Task TryResolveAsync_NoBinding_ReturnsOriginalUnchanged()
    {
        var cfg = """{"amount":"100","recipient":"abc"}""";
        var node = MakeNode("myNode", cfg);
        var action = new ActionModel { Id = ActionId, UserId = UserId, Nodes = [node], Edges = [] };

        var r = await MakeResolver().TryResolveAsync(
            cfg, node, action, new Dictionary<Guid, ActionNodeExecution>(),
            new Dictionary<Guid, ActionNodeExecution>(), UserId, CancellationToken.None);

        r.Ok.Should().BeTrue();
        r.ResolvedJson.Should().Be(cfg);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TryResolveAsync_NullOrEmpty_ReturnsOk(string? cfg)
    {
        var node = MakeNode("n", cfg);
        var action = new ActionModel { Id = ActionId, UserId = UserId, Nodes = [node], Edges = [] };

        var r = await MakeResolver().TryResolveAsync(
            cfg, node, action, new Dictionary<Guid, ActionNodeExecution>(),
            new Dictionary<Guid, ActionNodeExecution>(), UserId, CancellationToken.None);

        r.Ok.Should().BeTrue();
    }

    // ── Upstream binding — happy path ─────────────────────────────────────────

    [Fact]
    public async Task TryResolveAsync_UpstreamBinding_SubstitutesValue()
    {
        var sourceId = Guid.NewGuid();
        var source = MakeNodeWithId(sourceId, "gate");
        var target = MakeNodeWithId(NodeId, "transfer",
            """{"amount":{"$from":"upstream.gate.amount"}}""");
        var action = MakeActionWithEdge(source, target);

        var exec = MakeExec(sourceId, """{"amount":"500","recipient":"xyz"}""");
        var upstreamExecs = new Dictionary<Guid, ActionNodeExecution> { [sourceId] = exec };

        var r = await MakeResolver().TryResolveAsync(
            target.Config, target, action, upstreamExecs,
            new Dictionary<Guid, ActionNodeExecution>(), UserId, CancellationToken.None);

        r.Ok.Should().BeTrue(because: r.Error ?? "no error");
        var doc = JsonDocument.Parse(r.ResolvedJson!);
        doc.RootElement.GetProperty("amount").GetString().Should().Be("500");
    }

    [Fact]
    public async Task TryResolveAsync_NestedProperty_SubstitutesCorrectly()
    {
        var sourceId = Guid.NewGuid();
        var source = MakeNodeWithId(sourceId, "gate");
        var target = MakeNodeWithId(NodeId, "transfer",
            """{"config":{"amount":{"$from":"upstream.gate.info.value"}}}""");
        var action = MakeActionWithEdge(source, target);

        var exec = MakeExec(sourceId, """{"info":{"value":"42"}}""");
        var upstreamExecs = new Dictionary<Guid, ActionNodeExecution> { [sourceId] = exec };

        var r = await MakeResolver().TryResolveAsync(
            target.Config, target, action, upstreamExecs,
            new Dictionary<Guid, ActionNodeExecution>(), UserId, CancellationToken.None);

        r.Ok.Should().BeTrue(because: r.Error ?? "no error");
        var doc = JsonDocument.Parse(r.ResolvedJson!);
        doc.RootElement.GetProperty("config").GetProperty("amount").GetString().Should().Be("42");
    }

    // ── Upstream binding — fail closed ────────────────────────────────────────

    [Fact]
    public async Task TryResolveAsync_MissingUpstreamMember_ReturnsFalseWithError()
    {
        var sourceId = Guid.NewGuid();
        var source = MakeNodeWithId(sourceId, "gate");
        var target = MakeNodeWithId(NodeId, "transfer",
            """{"amount":{"$from":"upstream.gate.nonExistent"}}""");
        var action = MakeActionWithEdge(source, target);

        var exec = MakeExec(sourceId, """{"amount":"100"}""");
        var upstreamExecs = new Dictionary<Guid, ActionNodeExecution> { [sourceId] = exec };

        var r = await MakeResolver().TryResolveAsync(
            target.Config, target, action, upstreamExecs,
            new Dictionary<Guid, ActionNodeExecution>(), UserId, CancellationToken.None);

        r.Ok.Should().BeFalse();
        r.Error.Should().Contain("nonExistent");
    }

    [Fact]
    public async Task TryResolveAsync_UpstreamNodeNotInScope_ReturnsFalseWithError()
    {
        var sourceId = Guid.NewGuid();
        var source = MakeNodeWithId(sourceId, "gate");
        var target = MakeNodeWithId(NodeId, "transfer",
            """{"amount":{"$from":"upstream.otherNode.amount"}}""");
        var action = MakeActionWithEdge(source, target);

        var exec = MakeExec(sourceId, """{"amount":"100"}""");
        var upstreamExecs = new Dictionary<Guid, ActionNodeExecution> { [sourceId] = exec };

        var r = await MakeResolver().TryResolveAsync(
            target.Config, target, action, upstreamExecs,
            new Dictionary<Guid, ActionNodeExecution>(), UserId, CancellationToken.None);

        r.Ok.Should().BeFalse();
        r.Error.Should().Contain("otherNode");
    }

    // ── Holon binding — happy path ────────────────────────────────────────────

    [Fact]
    public async Task TryResolveAsync_HolonBinding_SubstitutesField()
    {
        var holonId = Guid.NewGuid();
        var holon = MakeHolon(UserId, new Dictionary<string, string> { ["status"] = "FUNDED" });
        var holonManager = MakeHolonManager(holon, holonId);

        var cfg = $"{{\"status\":{{\"$from\":\"holon.{holonId}.status\"}}}}";
        var target = MakeNodeWithId(NodeId, "check", cfg);
        var action = new ActionModel { Id = ActionId, UserId = UserId, Nodes = [target], Edges = [] };

        var r = await new ActionConfigBindingResolver(holonManager).TryResolveAsync(
            cfg, target, action, new Dictionary<Guid, ActionNodeExecution>(),
            new Dictionary<Guid, ActionNodeExecution>(), UserId, CancellationToken.None);

        r.Ok.Should().BeTrue(because: r.Error ?? "no error");
        var doc = JsonDocument.Parse(r.ResolvedJson!);
        doc.RootElement.GetProperty("status").GetString().Should().Be("FUNDED");
    }

    // ── Holon binding — fail closed ───────────────────────────────────────────

    [Fact]
    public async Task TryResolveAsync_NonOwnedHolon_ReturnsFalseWithError()
    {
        var holonId = Guid.NewGuid();
        var holon = MakeHolon(Guid.NewGuid()); // owned by a different user
        var holonManager = MakeHolonManager(holon, holonId);

        var cfg = $"{{\"x\":{{\"$from\":\"holon.{holonId}.status\"}}}}";
        var target = MakeNodeWithId(NodeId, "check", cfg);
        var action = new ActionModel { Id = ActionId, UserId = UserId, Nodes = [target], Edges = [] };

        var r = await new ActionConfigBindingResolver(holonManager).TryResolveAsync(
            cfg, target, action, new Dictionary<Guid, ActionNodeExecution>(),
            new Dictionary<Guid, ActionNodeExecution>(), UserId, CancellationToken.None);

        r.Ok.Should().BeFalse();
        r.Error.Should().Contain("not found or not accessible");
    }

    [Fact]
    public async Task TryResolveAsync_HolonNotFound_ReturnsFalseWithError()
    {
        var holonId = Guid.NewGuid();
        var cfg = $"{{\"x\":{{\"$from\":\"holon.{holonId}.status\"}}}}";
        var target = MakeNodeWithId(NodeId, "check", cfg);
        var action = new ActionModel { Id = ActionId, UserId = UserId, Nodes = [target], Edges = [] };

        var r = await MakeResolver().TryResolveAsync(
            cfg, target, action, new Dictionary<Guid, ActionNodeExecution>(),
            new Dictionary<Guid, ActionNodeExecution>(), UserId, CancellationToken.None);

        r.Ok.Should().BeFalse();
        r.Error.Should().Contain("not found or not accessible");
    }

    // ── Structural violations (definition-time checks) ────────────────────────

    [Fact]
    public void FindAndValidateBindings_ExtraKeys_ReturnsError()
    {
        var cfg = """{"amount":{"$from":"upstream.gate.amount","extra":"oops"}}""";
        var err = ActionConfigBindingResolver.FindAndValidateBindings(cfg, out _);

        err.Should().NotBeNull().And.Contain("exactly one key");
    }

    [Fact]
    public void FindAndValidateBindings_ArrayElement_ReturnsError()
    {
        var cfg = """{"items":[{"$from":"upstream.gate.amount"}]}""";
        var err = ActionConfigBindingResolver.FindAndValidateBindings(cfg, out _);

        err.Should().NotBeNull().And.Contain("array element");
    }

    [Fact]
    public void FindAndValidateBindings_ValidBinding_CollectsPaths()
    {
        var cfg = """{"amount":{"$from":"upstream.gate.amount"},"to":{"$from":"upstream.gate.recipient"}}""";
        var err = ActionConfigBindingResolver.FindAndValidateBindings(cfg, out var paths);

        err.Should().BeNull();
        paths.Should().HaveCount(2);
        paths.Should().Contain("upstream.gate.amount");
        paths.Should().Contain("upstream.gate.recipient");
    }

    [Fact]
    public void FindAndValidateBindings_NoBindings_ReturnsNullAndEmptyPaths()
    {
        var cfg = """{"amount":"100"}""";
        var err = ActionConfigBindingResolver.FindAndValidateBindings(cfg, out var paths);

        err.Should().BeNull();
        paths.Should().BeEmpty();
    }

    // ── IHolon test double ────────────────────────────────────────────────────

    private sealed class TestHolon : IHolon
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public Guid? ParentHolonId { get; set; }
        public Guid? UserId { get; set; }
        public string ProviderName { get; set; } = string.Empty;
        public string? ChainId { get; set; }
        public string? AssetType { get; set; }
        public string? TokenId { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = [];
        public List<Guid> PeerHolonIds { get; set; } = [];
        public DateTime CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public bool IsActive { get; set; }
        public Guid? SourceHolonId { get; set; }
        public Guid? OriginUserId { get; set; }
        public bool IsPublic { get; set; }
    }
}
