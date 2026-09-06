using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;
using ActionNodeTemplateEntity = AZOA.WebAPI.Models.Actions.ActionNodeTemplate;
using ActionTemplateEntity = AZOA.WebAPI.Models.Actions.ActionTemplate;
using ActionTemplateNodeEntity = AZOA.WebAPI.Models.Actions.ActionTemplateNode;
using ActionTemplateEdgeEntity = AZOA.WebAPI.Models.Actions.ActionTemplateEdge;

namespace AZOA.WebAPI.Tests.Actions;

public class ActionInstantiatorTests
{
    private readonly ActionInstantiator _instantiator;
    private readonly Mock<IActionTemplateStore> _templateStoreMock;
    private readonly Mock<IActionDagValidator> _validatorMock;

    public ActionInstantiatorTests()
    {
        _templateStoreMock = new Mock<IActionTemplateStore>();

        _validatorMock = new Mock<IActionDagValidator>();
        _validatorMock.Setup(v => v.Validate(It.IsAny<ActionEntity>(), It.IsAny<bool>()))
            .Returns(new DagValidationResult { IsValid = true });

        _instantiator = new ActionInstantiator(
            _templateStoreMock.Object, _validatorMock.Object);
    }

    [Fact]
    public async Task InstantiateAsync_TemplateNotFound_Throws()
    {
        _templateStoreMock
            .Setup(s => s.GetTemplateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActionTemplateEntity?)null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _instantiator.InstantiateAsync(Guid.NewGuid(), "{}", Guid.NewGuid()));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task InstantiateAsync_ValidTemplate_CreatesAction()
    {
        var templateId = Guid.NewGuid();
        var nodeTemplateId = Guid.NewGuid();
        const string slot1Id = "start";
        const string slot2Id = "end";

        var nodeTemplate = new ActionNodeTemplateEntity
        {
            Id = nodeTemplateId,
            Name = "Create Holon",
            NodeType = ActionNodeType.HolonCreate,
            DefaultConfig = "{\"holonType\": \"default\"}",
        };

        var template = new ActionTemplateEntity
        {
            Id = templateId,
            Name = "Simple ActionDefinition",
            Description = "A simple action template",
            AuthorUserId = Guid.NewGuid(),
            Nodes = new List<ActionTemplateNodeEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TemplateId = templateId,
                    SlotId = slot1Id,
                    NodeTemplateId = nodeTemplateId,
                    IsEntry = true,
                    IsTerminal = false,
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    TemplateId = templateId,
                    SlotId = slot2Id,
                    NodeTemplateId = nodeTemplateId,
                    IsEntry = false,
                    IsTerminal = true,
                },
            },
            Edges = new List<ActionTemplateEdgeEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TemplateId = templateId,
                    SourceSlotId = slot1Id,
                    TargetSlotId = slot2Id,
                },
            },
        };

        _templateStoreMock
            .Setup(s => s.GetTemplateAsync(templateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(template);
        _templateStoreMock
            .Setup(s => s.GetNodeTemplateAsync(nodeTemplateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodeTemplate);

        var userId = Guid.NewGuid();
        var action = await _instantiator.InstantiateAsync(templateId, "{}", userId);

        // Status moved to ActionRun (see action-temporal-fork-model ADR §2.2);
        // instantiation produces the definition only.
        Assert.NotNull(action);
        Assert.Equal("Simple ActionDefinition", action.Name);
        Assert.Equal(userId, action.UserId);
        Assert.Equal(templateId, action.TemplateId);
        Assert.Equal(2, action.Nodes.Count);
        Assert.Equal(1, action.Edges.Count);
        Assert.Single(action.Nodes.Where(n => n.IsEntry));
        Assert.Single(action.Nodes.Where(n => n.IsTerminal));
    }

    [Fact]
    public async Task InstantiateAsync_RequiredParamMissing_Throws()
    {
        var templateId = Guid.NewGuid();

        var template = new ActionTemplateEntity
        {
            Id = templateId,
            Name = "Param ActionDefinition",
            AuthorUserId = Guid.NewGuid(),
            Parameters = "{\"required\":[\"holonId\"]}",
            Nodes = new List<ActionTemplateNodeEntity>(),
            Edges = new List<ActionTemplateEdgeEntity>(),
        };

        _templateStoreMock
            .Setup(s => s.GetTemplateAsync(templateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(template);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _instantiator.InstantiateAsync(templateId, "{}", Guid.NewGuid()));
        Assert.Contains("Required parameter", ex.Message);
    }

    [Fact]
    public async Task InstantiateAsync_MissingNodeTemplate_Throws()
    {
        var templateId = Guid.NewGuid();
        var missingNodeTemplateId = Guid.NewGuid();

        var template = new ActionTemplateEntity
        {
            Id = templateId,
            Name = "Broken Template",
            AuthorUserId = Guid.NewGuid(),
            Nodes = new List<ActionTemplateNodeEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TemplateId = templateId,
                    SlotId = "step_1",
                    NodeTemplateId = missingNodeTemplateId,
                    IsEntry = true,
                    IsTerminal = true,
                },
            },
            Edges = new List<ActionTemplateEdgeEntity>(),
        };

        _templateStoreMock
            .Setup(s => s.GetTemplateAsync(templateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(template);
        _templateStoreMock
            .Setup(s => s.GetNodeTemplateAsync(missingNodeTemplateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActionNodeTemplateEntity?)null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _instantiator.InstantiateAsync(templateId, "{}", Guid.NewGuid()));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task InstantiateAsync_DagValidationFails_Throws()
    {
        var templateId = Guid.NewGuid();
        var nodeTemplateId = Guid.NewGuid();

        var nodeTemplate = new ActionNodeTemplateEntity
        {
            Id = nodeTemplateId,
            Name = "Test Node",
            NodeType = ActionNodeType.HolonCreate,
            DefaultConfig = "{}",
        };

        var template = new ActionTemplateEntity
        {
            Id = templateId,
            Name = "Invalid DAG Template",
            AuthorUserId = Guid.NewGuid(),
            Nodes = new List<ActionTemplateNodeEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TemplateId = templateId,
                    SlotId = "step_1",
                    NodeTemplateId = nodeTemplateId,
                    IsEntry = true,
                    IsTerminal = true,
                },
            },
            Edges = new List<ActionTemplateEdgeEntity>(),
        };

        _templateStoreMock
            .Setup(s => s.GetTemplateAsync(templateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(template);
        _templateStoreMock
            .Setup(s => s.GetNodeTemplateAsync(nodeTemplateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodeTemplate);

        _validatorMock.Setup(v => v.Validate(It.IsAny<ActionEntity>(), It.IsAny<bool>()))
            .Returns(new DagValidationResult
            {
                IsValid = false,
                Errors = new List<string> { "No terminal node found" },
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _instantiator.InstantiateAsync(templateId, "{}", Guid.NewGuid()));
        Assert.Contains("invalid DAG", ex.Message);
    }
}
