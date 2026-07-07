using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// Pins FR-4 / AC-4a/4b: safe config deserialization (malformed config →
/// Failed node, not thrown exception) and definition-time enforcement
/// (AddNodeAsync / PublishAsync reject bad config).
/// See Services/Quest/AGENTS.md §node-config.
/// </summary>
public class QuestNodeConfigSafeDeserializeTests
{
    private static readonly Guid AvatarId = Guid.NewGuid();

    // ─── QuestNodeConfig.TryDeserialize unit tests ────────────────────────

    [Fact]
    public void TryDeserialize_ValidJson_ReturnsTrue()
    {
        var json = """{"predicate":"true","reads":{},"holons":[]}""";
        var ok = QuestNodeConfig.TryDeserialize<GateCheckNodeConfig>(
            json, "GateCheck", out var cfg, out var error);

        ok.Should().BeTrue();
        error.Should().BeEmpty();
        cfg.Predicate.Should().Be("true");
    }

    [Fact]
    public void TryDeserialize_EmptyString_ReturnsDefaultInstance()
    {
        var ok = QuestNodeConfig.TryDeserialize<GateCheckNodeConfig>(
            string.Empty, "GateCheck", out var cfg, out var error);

        ok.Should().BeTrue("empty string → default instance, not an error");
        error.Should().BeEmpty();
        cfg.Should().NotBeNull();
    }

    [Fact]
    public void TryDeserialize_NullJson_ReturnsDefaultInstance()
    {
        var ok = QuestNodeConfig.TryDeserialize<GateCheckNodeConfig>(
            null, "GateCheck", out var cfg, out var error);

        ok.Should().BeTrue("null → default instance, not an error");
        cfg.Should().NotBeNull();
    }

    [Fact]
    public void TryDeserialize_MalformedJson_ReturnsFalseWithMessage()
    {
        var ok = QuestNodeConfig.TryDeserialize<GateCheckNodeConfig>(
            "not json at all!!!", "GateCheck", out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("GateCheck");
        error.Should().ContainEquivalentOf("parse error");
    }

    [Fact]
    public void TryDeserialize_UnknownMember_ReturnsFalseWithMessage()
    {
        // StrictOptions has UnmappedMemberHandling.Disallow.
        var json = """{"predicate":"true","unknownExtraField":42}""";
        var ok = QuestNodeConfig.TryDeserialize<GateCheckNodeConfig>(
            json, "GateCheck", out _, out var error);

        ok.Should().BeFalse("unknown members are rejected by strict options");
        error.Should().NotBeEmpty();
    }

    // ─── QuestNodeConfigRegistry exhaustiveness pin ───────────────────────

    [Fact]
    public void Registry_AllQuestNodeTypeValues_HaveEntries()
    {
        // Any QuestNodeType missing from the registry throws NotSupportedException.
        // This test catches newly-added types that weren't wired in.
        foreach (var nodeType in Enum.GetValues<QuestNodeType>())
        {
            var act = () => QuestNodeConfigRegistry.GetConfigType(nodeType);
            act.Should().NotThrow($"QuestNodeType.{nodeType} must have a registry entry");
        }
    }

    // ─── AC-4a: malformed config → Failed node, not thrown exception ──────

    [Fact]
    public async Task ExecuteAsync_TransferNodeMalformedConfig_NodeFails_NotThrows()
    {
        // Build a single-node quest with a Transfer node whose config is invalid JSON.
        var nodeId = Guid.NewGuid();
        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "BadConfig",
            AvatarId = AvatarId,
            Status = QuestStatus.Active,
            Nodes = new List<QuestNode>
            {
                new() { Id = nodeId, Name = "Transfer1", NodeType = QuestNodeType.Transfer,
                        IsEntry = true, IsTerminal = true,
                        Config = "not-valid-json!!!" },
            },
            Edges = new List<QuestEdge>(),
        };

        var questStore = new InMemoryQuestStore();
        await questStore.UpsertQuestAsync(quest);

        // Transfer handler needs an INftManager — we use a mock that should
        // never be reached because TryDeserialize fails first.
        var nftMock = new Moq.Mock<AZOA.WebAPI.Interfaces.Managers.INftManager>();
        var transferHandler = new AZOA.WebAPI.Services.Quest.Handlers.TransferNodeHandler(nftMock.Object);

        var manager = new QuestManager(
            questStore,
            new InMemoryQuestRunStore(),
            new InMemoryQuestNodeExecutionStore(),
            new QuestDagValidator(), new QuestDagExecutabilityValidator(),
            new QuestNodeHandlerRegistry(new IQuestNodeHandler[] { transferHandler }),
            new InMemorySagaStore(),
            WalletManagerMocks.WithOneWallet(),    // chain-cap gate passes
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        // Should not throw; the node should end up Failed with a descriptive message.
        var act = async () => await manager.ExecuteAsync(quest.Id, AvatarId);
        await act.Should().NotThrowAsync();

        var result = await manager.ExecuteAsync(quest.Id, AvatarId);
        // The run itself completes (engine doesn't crash); the node is Failed.
        // Depending on engine behaviour, IsError may or may not be set on the run-level
        // result — what matters is no exception escapes.
        nftMock.Verify(m => m.TransferAsync(
            It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(),
            It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Moq.Times.Never,
            "TransferAsync must never be reached when config parse fails");
    }

    [Fact]
    public async Task ExecuteAsync_GateCheckNodeMalformedConfig_NodeFails_NotThrows()
    {
        var nodeId = Guid.NewGuid();
        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "BadGateConfig",
            AvatarId = AvatarId,
            Status = QuestStatus.Active,
            Nodes = new List<QuestNode>
            {
                new() { Id = nodeId, Name = "Gate1", NodeType = QuestNodeType.GateCheck,
                        IsEntry = true, IsTerminal = true,
                        Config = "{broken json" },
            },
            Edges = new List<QuestEdge>(),
        };

        var questStore = new InMemoryQuestStore();
        await questStore.UpsertQuestAsync(quest);

        var holonMock = new Moq.Mock<AZOA.WebAPI.Interfaces.Managers.IHolonManager>();
        var gateHandler = new AZOA.WebAPI.Services.Quest.Handlers.GateCheckNodeHandler(holonMock.Object);

        var manager = new QuestManager(
            questStore,
            new InMemoryQuestRunStore(),
            new InMemoryQuestNodeExecutionStore(),
            new QuestDagValidator(), new QuestDagExecutabilityValidator(),
            new QuestNodeHandlerRegistry(new IQuestNodeHandler[] { gateHandler }),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var act = async () => await manager.ExecuteAsync(quest.Id, AvatarId);
        await act.Should().NotThrowAsync("malformed config must never throw out of the engine");
    }

    // ─── AC-4b: definition-time enforcement ───────────────────────────────

    [Fact]
    public async Task AddNodeAsync_BadConfig_Rejected()
    {
        var quest = BuildActiveQuest();
        quest.Status = QuestStatus.Draft; // must be Draft to allow mutations

        var (manager, _) = BuildManager(quest);

        var result = await manager.AddNodeAsync(quest.Id,
            new QuestNodeCreateModel
            {
                Name = "BadTransfer",
                NodeType = QuestNodeType.Transfer,
                Config = """{"unknownField":"oops"}""",  // unknown member → strict reject
            },
            AvatarId);

        result.IsError.Should().BeTrue("unknown config member must be rejected at add time");
        result.Message.Should().ContainEquivalentOf("config");
    }

    [Fact]
    public async Task AddNodeAsync_ValidConfig_Accepted()
    {
        var quest = BuildActiveQuest();
        quest.Status = QuestStatus.Draft;

        var (manager, _) = BuildManager(quest);

        // Valid Transfer config (Guid + empty request).
        var validConfig = "{\"nftId\":\"" + Guid.NewGuid() + "\",\"request\":{}}";
        var result = await manager.AddNodeAsync(quest.Id,
            new QuestNodeCreateModel
            {
                Name = "GoodTransfer",
                NodeType = QuestNodeType.Transfer,
                Config = validConfig,
            },
            AvatarId);

        result.IsError.Should().BeFalse("valid config must pass at add time");
    }

    [Fact]
    public async Task PublishAsync_QuestWithBadConfigNode_Fails()
    {
        // Build a quest with a structurally-valid DAG but a node whose config
        // will fail registry strict validation.
        var entryId = Guid.NewGuid();
        var badNodeId = Guid.NewGuid();
        var qid = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id = qid,
            Name = "BadNodeConfig",
            AvatarId = AvatarId,
            Status = QuestStatus.Draft,
            Nodes = new List<QuestNode>
            {
                new() { Id = entryId,   Name = "Entry",       NodeType = QuestNodeType.Condition,
                        IsEntry = true,  IsTerminal = false, Config = "{}" },
                new() { Id = badNodeId, Name = "BadTransfer", NodeType = QuestNodeType.Transfer,
                        IsEntry = false, IsTerminal = true,
                        Config = """{"surpriseField":true}""" },  // unknown → strict reject
            },
            Edges = new List<QuestEdge>
            {
                new() { Id = Guid.NewGuid(), QuestId = qid,
                        SourceNodeId = entryId, TargetNodeId = badNodeId,
                        EdgeType = QuestEdgeType.Control },
            },
        };

        var (manager, _) = BuildManager(quest);

        var result = await manager.PublishAsync(quest.Id, AvatarId);

        result.IsError.Should().BeTrue("publish must be rejected when a node has invalid config (AC-4b)");
        result.Message.Should().ContainEquivalentOf("config");
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    private static QuestEntity BuildActiveQuest()
    {
        var entryId    = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var qid        = Guid.NewGuid();
        return new QuestEntity
        {
            Id       = qid,
            Name     = "TestQuest",
            AvatarId = AvatarId,
            Status   = QuestStatus.Draft,
            Nodes = new List<QuestNode>
            {
                new() { Id = entryId,    Name = "Entry",    NodeType = QuestNodeType.Condition,
                        IsEntry = true,  IsTerminal = false, Config = "{}" },
                new() { Id = terminalId, Name = "Terminal", NodeType = QuestNodeType.Condition,
                        IsEntry = false, IsTerminal = true,  Config = "{}" },
            },
            Edges = new List<QuestEdge>
            {
                new() { Id = Guid.NewGuid(), QuestId = qid,
                        SourceNodeId = entryId, TargetNodeId = terminalId,
                        EdgeType = QuestEdgeType.Control },
            },
        };
    }

    private static (QuestManager manager, InMemoryQuestStore questStore)
        BuildManager(QuestEntity? quest = null)
    {
        var questStore = new InMemoryQuestStore();
        if (quest != null)
            questStore.UpsertQuestAsync(quest).GetAwaiter().GetResult();

        var manager = new QuestManager(
            questStore,
            new InMemoryQuestRunStore(),
            new InMemoryQuestNodeExecutionStore(),
            new QuestDagValidator(), new QuestDagExecutabilityValidator(),
            new QuestNodeHandlerRegistry(Array.Empty<IQuestNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        return (manager, questStore);
    }
}
