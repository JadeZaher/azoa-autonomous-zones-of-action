using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Services;
using Xunit;
using QuestEntity = OASIS.WebAPI.Models.Quest.Quest;
using QuestNodeEntity = OASIS.WebAPI.Models.Quest.QuestNode;
using QuestEdgeEntity = OASIS.WebAPI.Models.Quest.QuestEdge;

namespace OASIS.WebAPI.Tests.Quest;

public class QuestDagValidatorTests
{
    private readonly QuestDagValidator _validator = new();

    [Fact]
    public void Validate_EmptyQuest_ReturnsInvalid()
    {
        var quest = new QuestEntity { Id = Guid.NewGuid(), Name = "Empty" };
        var result = _validator.Validate(quest);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no nodes"));
    }

    [Fact]
    public void Validate_ValidLinearGraph_ReturnsValid()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var cId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Linear Quest",
            Nodes = new List<QuestNodeEntity>
            {
                new() { Id = aId, Name = "A", IsEntry = true, IsTerminal = false },
                new() { Id = bId, Name = "B", IsEntry = false, IsTerminal = false },
                new() { Id = cId, Name = "C", IsEntry = false, IsTerminal = true }
            },
            Edges = new List<QuestEdgeEntity>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = aId, TargetNodeId = bId },
                new() { Id = Guid.NewGuid(), SourceNodeId = bId, TargetNodeId = cId }
            }
        };

        var result = _validator.Validate(quest);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.TopologicalOrder.Count);
        Assert.True(result.TopologicalOrder.IndexOf(aId) < result.TopologicalOrder.IndexOf(bId));
        Assert.True(result.TopologicalOrder.IndexOf(bId) < result.TopologicalOrder.IndexOf(cId));
    }

    [Fact]
    public void Validate_CyclicGraph_ReturnsInvalid()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var cId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Cyclic Quest",
            Nodes = new List<QuestNodeEntity>
            {
                new() { Id = aId, Name = "A", IsEntry = true, IsTerminal = false },
                new() { Id = bId, Name = "B", IsEntry = false, IsTerminal = false },
                new() { Id = cId, Name = "C", IsEntry = false, IsTerminal = true }
            },
            Edges = new List<QuestEdgeEntity>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = aId, TargetNodeId = bId },
                new() { Id = Guid.NewGuid(), SourceNodeId = bId, TargetNodeId = cId },
                new() { Id = Guid.NewGuid(), SourceNodeId = cId, TargetNodeId = aId }
            }
        };

        var result = _validator.Validate(quest);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Cycle"));
    }

    [Fact]
    public void Validate_NoEntryNode_ReturnsInvalid()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "No Entry",
            Nodes = new List<QuestNodeEntity>
            {
                new() { Id = aId, Name = "A", IsEntry = false, IsTerminal = false },
                new() { Id = bId, Name = "B", IsEntry = false, IsTerminal = true }
            },
            Edges = new List<QuestEdgeEntity>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = aId, TargetNodeId = bId }
            }
        };

        var result = _validator.Validate(quest);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("entry"));
    }

    [Fact]
    public void Validate_NoTerminalNode_ReturnsInvalid()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "No Terminal",
            Nodes = new List<QuestNodeEntity>
            {
                new() { Id = aId, Name = "A", IsEntry = true, IsTerminal = false },
                new() { Id = bId, Name = "B", IsEntry = false, IsTerminal = false }
            },
            Edges = new List<QuestEdgeEntity>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = aId, TargetNodeId = bId }
            }
        };

        var result = _validator.Validate(quest);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("terminal"));
    }

    [Fact]
    public void Validate_OrphanNode_ReturnsInvalid()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var cId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Orphan",
            Nodes = new List<QuestNodeEntity>
            {
                new() { Id = aId, Name = "A", IsEntry = true, IsTerminal = false },
                new() { Id = bId, Name = "B", IsEntry = false, IsTerminal = true },
                new() { Id = cId, Name = "C", IsEntry = false, IsTerminal = false }
            },
            Edges = new List<QuestEdgeEntity>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = aId, TargetNodeId = bId }
            }
        };

        var result = _validator.Validate(quest);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Orphan"));
    }

    [Fact]
    public void Validate_ValidDiamondGraph_ReturnsValid()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var cId = Guid.NewGuid();
        var dId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Diamond",
            Nodes = new List<QuestNodeEntity>
            {
                new() { Id = aId, Name = "A", IsEntry = true, IsTerminal = false },
                new() { Id = bId, Name = "B", IsEntry = false, IsTerminal = false },
                new() { Id = cId, Name = "C", IsEntry = false, IsTerminal = false },
                new() { Id = dId, Name = "D", IsEntry = false, IsTerminal = true }
            },
            Edges = new List<QuestEdgeEntity>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = aId, TargetNodeId = bId },
                new() { Id = Guid.NewGuid(), SourceNodeId = aId, TargetNodeId = cId },
                new() { Id = Guid.NewGuid(), SourceNodeId = bId, TargetNodeId = dId },
                new() { Id = Guid.NewGuid(), SourceNodeId = cId, TargetNodeId = dId }
            }
        };

        var result = _validator.Validate(quest);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(4, result.TopologicalOrder.Count);
        Assert.Equal(aId, result.TopologicalOrder[0]);
        Assert.Equal(dId, result.TopologicalOrder[3]);
    }

    [Fact]
    public void Validate_ExecutionOrderIsSet()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var cId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Order Test",
            Nodes = new List<QuestNodeEntity>
            {
                new() { Id = aId, Name = "A", IsEntry = true, IsTerminal = false },
                new() { Id = bId, Name = "B", IsEntry = false, IsTerminal = false },
                new() { Id = cId, Name = "C", IsEntry = false, IsTerminal = true }
            },
            Edges = new List<QuestEdgeEntity>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = aId, TargetNodeId = bId },
                new() { Id = Guid.NewGuid(), SourceNodeId = bId, TargetNodeId = cId }
            }
        };

        _validator.Validate(quest);
        var nodeA = quest.Nodes.First(n => n.Id == aId);
        var nodeB = quest.Nodes.First(n => n.Id == bId);
        var nodeC = quest.Nodes.First(n => n.Id == cId);
        Assert.True(nodeA.ExecutionOrder < nodeB.ExecutionOrder);
        Assert.True(nodeB.ExecutionOrder < nodeC.ExecutionOrder);
    }
}
