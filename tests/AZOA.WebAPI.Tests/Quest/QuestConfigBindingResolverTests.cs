using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest;
using Xunit;
using QuestModel = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// Unit tests for <see cref="QuestConfigBindingResolver"/>: binding resolution
/// (upstream + holon), fail-closed paths, structural violations, and shadow
/// round-trip acceptance (AC-1a..1f).
/// </summary>
public class QuestConfigBindingResolverTests
{
    private static readonly Guid AvatarId = Guid.NewGuid();
    private static readonly Guid NodeId   = Guid.NewGuid();
    private static readonly Guid QuestId  = Guid.NewGuid();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static QuestNode MakeNode(string name, string? config = null) =>
        new() { Id = NodeId, QuestId = QuestId, Name = name, Config = config ?? "{}" };

    private static QuestNode MakeNodeWithId(Guid id, string name, string? config = null) =>
        new() { Id = id, QuestId = QuestId, Name = name, Config = config ?? "{}" };

    private static QuestNodeExecution MakeExec(Guid nodeId, string output) =>
        new()
        {
            Id = Guid.NewGuid(), RunId = Guid.NewGuid(), NodeId = nodeId,
            State = QuestNodeState.Succeeded, Output = output
        };

    private static IHolon MakeHolon(Guid avatarId, Dictionary<string, string>? metadata = null) =>
        new TestHolon
        {
            Id = Guid.NewGuid(), Name = "TestHolon", AvatarId = avatarId,
            Metadata = metadata ?? [], IsActive = true,
            PeerHolonIds = [], CreatedDate = DateTime.UtcNow
        };

    private static IHolonManager MakeHolonManager(IHolon? holon = null, Guid? holonId = null)
    {
        var mock = new Mock<IHolonManager>();
        if (holon is not null && holonId is not null)
        {
            mock.Setup(m => m.GetAsync(holonId.Value, null))
                .ReturnsAsync(new AZOAResult<IHolon> { Result = holon });
        }
        else
        {
            mock.Setup(m => m.GetAsync(It.IsAny<Guid>(), null))
                .ReturnsAsync(new AZOAResult<IHolon> { IsError = true, Message = "not found" });
        }
        return mock.Object;
    }

    private static QuestModel MakeQuestWithEdge(QuestNode source, QuestNode target) =>
        new()
        {
            Id = QuestId, AvatarId = AvatarId,
            Nodes = [source, target],
            Edges =
            [
                new QuestEdge
                {
                    Id = Guid.NewGuid(), QuestId = QuestId,
                    SourceNodeId = source.Id, TargetNodeId = target.Id,
                    EdgeType = QuestEdgeType.Control
                }
            ]
        };

    private static QuestConfigBindingResolver MakeResolver(IHolonManager? holonManager = null) =>
        new(holonManager ?? MakeHolonManager());

    // ── No-binding passthrough ────────────────────────────────────────────────

    [Fact]
    public async Task TryResolveAsync_NoBinding_ReturnsOriginalUnchanged()
    {
        var cfg = """{"amount":"100","recipient":"abc"}""";
        var node = MakeNode("myNode", cfg);
        var quest = new QuestModel { Id = QuestId, AvatarId = AvatarId, Nodes = [node], Edges = [] };

        var r = await MakeResolver().TryResolveAsync(
            cfg, node, quest, new Dictionary<Guid, QuestNodeExecution>(),
            new Dictionary<Guid, QuestNodeExecution>(), AvatarId, CancellationToken.None);

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
        var quest = new QuestModel { Id = QuestId, AvatarId = AvatarId, Nodes = [node], Edges = [] };

        var r = await MakeResolver().TryResolveAsync(
            cfg, node, quest, new Dictionary<Guid, QuestNodeExecution>(),
            new Dictionary<Guid, QuestNodeExecution>(), AvatarId, CancellationToken.None);

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
        var quest = MakeQuestWithEdge(source, target);

        var exec = MakeExec(sourceId, """{"amount":"500","recipient":"xyz"}""");
        var upstreamExecs = new Dictionary<Guid, QuestNodeExecution> { [sourceId] = exec };

        var r = await MakeResolver().TryResolveAsync(
            target.Config, target, quest, upstreamExecs,
            new Dictionary<Guid, QuestNodeExecution>(), AvatarId, CancellationToken.None);

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
        var quest = MakeQuestWithEdge(source, target);

        var exec = MakeExec(sourceId, """{"info":{"value":"42"}}""");
        var upstreamExecs = new Dictionary<Guid, QuestNodeExecution> { [sourceId] = exec };

        var r = await MakeResolver().TryResolveAsync(
            target.Config, target, quest, upstreamExecs,
            new Dictionary<Guid, QuestNodeExecution>(), AvatarId, CancellationToken.None);

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
        var quest = MakeQuestWithEdge(source, target);

        var exec = MakeExec(sourceId, """{"amount":"100"}""");
        var upstreamExecs = new Dictionary<Guid, QuestNodeExecution> { [sourceId] = exec };

        var r = await MakeResolver().TryResolveAsync(
            target.Config, target, quest, upstreamExecs,
            new Dictionary<Guid, QuestNodeExecution>(), AvatarId, CancellationToken.None);

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
        var quest = MakeQuestWithEdge(source, target);

        var exec = MakeExec(sourceId, """{"amount":"100"}""");
        var upstreamExecs = new Dictionary<Guid, QuestNodeExecution> { [sourceId] = exec };

        var r = await MakeResolver().TryResolveAsync(
            target.Config, target, quest, upstreamExecs,
            new Dictionary<Guid, QuestNodeExecution>(), AvatarId, CancellationToken.None);

        r.Ok.Should().BeFalse();
        r.Error.Should().Contain("otherNode");
    }

    // ── Holon binding — happy path ────────────────────────────────────────────

    [Fact]
    public async Task TryResolveAsync_HolonBinding_SubstitutesField()
    {
        var holonId = Guid.NewGuid();
        var holon = MakeHolon(AvatarId, new Dictionary<string, string> { ["status"] = "FUNDED" });
        var holonManager = MakeHolonManager(holon, holonId);

        var cfg = $"{{\"status\":{{\"$from\":\"holon.{holonId}.status\"}}}}";
        var target = MakeNodeWithId(NodeId, "check", cfg);
        var quest = new QuestModel { Id = QuestId, AvatarId = AvatarId, Nodes = [target], Edges = [] };

        var r = await new QuestConfigBindingResolver(holonManager).TryResolveAsync(
            cfg, target, quest, new Dictionary<Guid, QuestNodeExecution>(),
            new Dictionary<Guid, QuestNodeExecution>(), AvatarId, CancellationToken.None);

        r.Ok.Should().BeTrue(because: r.Error ?? "no error");
        var doc = JsonDocument.Parse(r.ResolvedJson!);
        doc.RootElement.GetProperty("status").GetString().Should().Be("FUNDED");
    }

    // ── Holon binding — fail closed ───────────────────────────────────────────

    [Fact]
    public async Task TryResolveAsync_NonOwnedHolon_ReturnsFalseWithError()
    {
        var holonId = Guid.NewGuid();
        var holon = MakeHolon(Guid.NewGuid()); // owned by a different avatar
        var holonManager = MakeHolonManager(holon, holonId);

        var cfg = $"{{\"x\":{{\"$from\":\"holon.{holonId}.status\"}}}}";
        var target = MakeNodeWithId(NodeId, "check", cfg);
        var quest = new QuestModel { Id = QuestId, AvatarId = AvatarId, Nodes = [target], Edges = [] };

        var r = await new QuestConfigBindingResolver(holonManager).TryResolveAsync(
            cfg, target, quest, new Dictionary<Guid, QuestNodeExecution>(),
            new Dictionary<Guid, QuestNodeExecution>(), AvatarId, CancellationToken.None);

        r.Ok.Should().BeFalse();
        r.Error.Should().Contain("not found or not accessible");
    }

    [Fact]
    public async Task TryResolveAsync_HolonNotFound_ReturnsFalseWithError()
    {
        var holonId = Guid.NewGuid();
        var cfg = $"{{\"x\":{{\"$from\":\"holon.{holonId}.status\"}}}}";
        var target = MakeNodeWithId(NodeId, "check", cfg);
        var quest = new QuestModel { Id = QuestId, AvatarId = AvatarId, Nodes = [target], Edges = [] };

        var r = await MakeResolver().TryResolveAsync(
            cfg, target, quest, new Dictionary<Guid, QuestNodeExecution>(),
            new Dictionary<Guid, QuestNodeExecution>(), AvatarId, CancellationToken.None);

        r.Ok.Should().BeFalse();
        r.Error.Should().Contain("not found or not accessible");
    }

    // ── Structural violations (definition-time checks) ────────────────────────

    [Fact]
    public void FindAndValidateBindings_ExtraKeys_ReturnsError()
    {
        var cfg = """{"amount":{"$from":"upstream.gate.amount","extra":"oops"}}""";
        var err = QuestConfigBindingResolver.FindAndValidateBindings(cfg, out _);

        err.Should().NotBeNull().And.Contain("exactly one key");
    }

    [Fact]
    public void FindAndValidateBindings_ArrayElement_ReturnsError()
    {
        var cfg = """{"items":[{"$from":"upstream.gate.amount"}]}""";
        var err = QuestConfigBindingResolver.FindAndValidateBindings(cfg, out _);

        err.Should().NotBeNull().And.Contain("array element");
    }

    [Fact]
    public void FindAndValidateBindings_ValidBinding_CollectsPaths()
    {
        var cfg = """{"amount":{"$from":"upstream.gate.amount"},"to":{"$from":"upstream.gate.recipient"}}""";
        var err = QuestConfigBindingResolver.FindAndValidateBindings(cfg, out var paths);

        err.Should().BeNull();
        paths.Should().HaveCount(2);
        paths.Should().Contain("upstream.gate.amount");
        paths.Should().Contain("upstream.gate.recipient");
    }

    [Fact]
    public void FindAndValidateBindings_NoBindings_ReturnsNullAndEmptyPaths()
    {
        var cfg = """{"amount":"100"}""";
        var err = QuestConfigBindingResolver.FindAndValidateBindings(cfg, out var paths);

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
        public Guid? AvatarId { get; set; }
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
        public Guid? OriginAvatarId { get; set; }
    }
}
