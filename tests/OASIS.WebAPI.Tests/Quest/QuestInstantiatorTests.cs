using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Services.Quest;
using Xunit;
using QuestEntity = OASIS.WebAPI.Models.Quest.Quest;
using QuestNodeTemplateEntity = OASIS.WebAPI.Models.Quest.QuestNodeTemplate;
using QuestTemplateEntity = OASIS.WebAPI.Models.Quest.QuestTemplate;
using QuestTemplateNodeEntity = OASIS.WebAPI.Models.Quest.QuestTemplateNode;
using QuestTemplateEdgeEntity = OASIS.WebAPI.Models.Quest.QuestTemplateEdge;

namespace OASIS.WebAPI.Tests.Quest;

public class QuestInstantiatorTests
{
    private readonly QuestInstantiator _instantiator;
    private readonly OASISDbContext _dbContext;
    private readonly Mock<IQuestDagValidator> _validatorMock;

    public QuestInstantiatorTests()
    {
        var options = new DbContextOptionsBuilder<OASISDbContext>()
            .UseInMemoryDatabase(databaseName: $"QuestInstantiator_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OASISDbContext(options);
        _validatorMock = new Mock<IQuestDagValidator>();
        _validatorMock.Setup(v => v.Validate(It.IsAny<QuestEntity>()))
            .Returns(new DagValidationResult { IsValid = true });

        var loggerMock = new Mock<ILogger<QuestInstantiator>>();
        _instantiator = new QuestInstantiator(_dbContext, _validatorMock.Object, loggerMock.Object);
    }

    [Fact]
    public async Task InstantiateAsync_TemplateNotFound_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _instantiator.InstantiateAsync(Guid.NewGuid(), "{}", Guid.NewGuid()));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task InstantiateAsync_ValidTemplate_CreatesQuest()
    {
        var templateId = Guid.NewGuid();
        var nodeTemplateId = Guid.NewGuid();
        var slot1Id = "start";
        var slot2Id = "end";

        await _dbContext.QuestNodeTemplates.AddAsync(new QuestNodeTemplateEntity
        {
            Id = nodeTemplateId,
            Name = "Create Holon",
            NodeType = QuestNodeType.HolonCreate,
            DefaultConfig = "{\"holonType\": \"default\"}"
        });

        await _dbContext.QuestTemplates.AddAsync(new QuestTemplateEntity
        {
            Id = templateId,
            Name = "Simple Quest",
            Description = "A simple quest template",
            AuthorAvatarId = Guid.NewGuid(),
            Nodes = new List<QuestTemplateNodeEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TemplateId = templateId,
                    SlotId = slot1Id,
                    NodeTemplateId = nodeTemplateId,
                    IsEntry = true,
                    IsTerminal = false
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    TemplateId = templateId,
                    SlotId = slot2Id,
                    NodeTemplateId = nodeTemplateId,
                    IsEntry = false,
                    IsTerminal = true
                }
            },
            Edges = new List<QuestTemplateEdgeEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TemplateId = templateId,
                    SourceSlotId = slot1Id,
                    TargetSlotId = slot2Id
                }
            }
        });
        await _dbContext.SaveChangesAsync();

        var avatarId = Guid.NewGuid();
        var quest = await _instantiator.InstantiateAsync(templateId, "{}", avatarId);

        // Status moved to QuestRun (see quest-temporal-fork-model ADR §2.2);
        // instantiation produces the definition only.
        Assert.NotNull(quest);
        Assert.Equal("Simple Quest", quest.Name);
        Assert.Equal(avatarId, quest.AvatarId);
        Assert.Equal(templateId, quest.TemplateId);
        Assert.Equal(2, quest.Nodes.Count);
        Assert.Equal(1, quest.Edges.Count);
        Assert.Single(quest.Nodes.Where(n => n.IsEntry));
        Assert.Single(quest.Nodes.Where(n => n.IsTerminal));
    }

    [Fact]
    public async Task InstantiateAsync_RequiredParamMissing_Throws()
    {
        var templateId = Guid.NewGuid();
        var nodeTemplateId = Guid.NewGuid();

        await _dbContext.QuestNodeTemplates.AddAsync(new QuestNodeTemplateEntity
        {
            Id = nodeTemplateId,
            Name = "Test Node",
            NodeType = QuestNodeType.HolonCreate,
            DefaultConfig = "{}"
        });

        await _dbContext.QuestTemplates.AddAsync(new QuestTemplateEntity
        {
            Id = templateId,
            Name = "Param Quest",
            AuthorAvatarId = Guid.NewGuid(),
            Parameters = "{\"required\":[\"holonId\"]}",
            Nodes = new List<QuestTemplateNodeEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TemplateId = templateId,
                    SlotId = "step_1",
                    NodeTemplateId = nodeTemplateId,
                    IsEntry = true,
                    IsTerminal = true
                }
            },
            Edges = new List<QuestTemplateEdgeEntity>()
        });
        await _dbContext.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _instantiator.InstantiateAsync(templateId, "{}", Guid.NewGuid()));
        Assert.Contains("Required parameter", ex.Message);
    }

    [Fact]
    public async Task InstantiateAsync_MissingNodeTemplate_Throws()
    {
        var templateId = Guid.NewGuid();
        var missingNodeTemplateId = Guid.NewGuid();

        await _dbContext.QuestTemplates.AddAsync(new QuestTemplateEntity
        {
            Id = templateId,
            Name = "Broken Template",
            AuthorAvatarId = Guid.NewGuid(),
            Nodes = new List<QuestTemplateNodeEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TemplateId = templateId,
                    SlotId = "step_1",
                    NodeTemplateId = missingNodeTemplateId,
                    IsEntry = true,
                    IsTerminal = true
                }
            },
            Edges = new List<QuestTemplateEdgeEntity>()
        });
        await _dbContext.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _instantiator.InstantiateAsync(templateId, "{}", Guid.NewGuid()));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task InstantiateAsync_DagValidationFails_Throws()
    {
        var templateId = Guid.NewGuid();
        var nodeTemplateId = Guid.NewGuid();

        await _dbContext.QuestNodeTemplates.AddAsync(new QuestNodeTemplateEntity
        {
            Id = nodeTemplateId,
            Name = "Test Node",
            NodeType = QuestNodeType.HolonCreate,
            DefaultConfig = "{}"
        });

        await _dbContext.QuestTemplates.AddAsync(new QuestTemplateEntity
        {
            Id = templateId,
            Name = "Invalid DAG Template",
            AuthorAvatarId = Guid.NewGuid(),
            Nodes = new List<QuestTemplateNodeEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TemplateId = templateId,
                    SlotId = "step_1",
                    NodeTemplateId = nodeTemplateId,
                    IsEntry = true,
                    IsTerminal = true
                }
            },
            Edges = new List<QuestTemplateEdgeEntity>()
        });
        await _dbContext.SaveChangesAsync();

        _validatorMock.Setup(v => v.Validate(It.IsAny<QuestEntity>()))
            .Returns(new DagValidationResult
            {
                IsValid = false,
                Errors = new List<string> { "No terminal node found" }
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _instantiator.InstantiateAsync(templateId, "{}", Guid.NewGuid()));
        Assert.Contains("invalid DAG", ex.Message);
    }
}
