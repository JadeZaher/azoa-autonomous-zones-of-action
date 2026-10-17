using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Services.Actions;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class ActionFrozenOccurrenceValidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PriorOccurrenceOnlyPublishesGuaranteedSuccessfulOutputs(bool handledFailure)
    {
        var source = Guid.NewGuid(); var handler = Guid.NewGuid(); var next = Guid.NewGuid();
        var graph = new ActionDefinition { Id = Guid.NewGuid(), Nodes =
        [
            new() { Id = source, Name = "Source", NodeType = ActionNodeType.Bridge, IsEntry = true, Config = "{}" },
            new() { Id = handler, Name = "Handler", NodeType = ActionNodeType.Condition, Config = "{}" },
            new() { Id = next, Name = "Next", NodeType = ActionNodeType.Back, IsEntry = true,
                Config = """{"BridgeTransactionId":{"$from":"run.Source.Id"}}""" },
        ], Edges = [new() { Id = Guid.NewGuid(), SourceNodeId = source, TargetNodeId = handler,
            EdgeType = handledFailure ? ActionEdgeType.OnFailure : ActionEdgeType.Control }] };
        var validator = new ActionDagExecutabilityValidator();
        validator.Validate(graph).IsValid.Should().BeFalse();
        var result = validator.ValidateFrozen(graph, [new DeploymentOccurrenceBoundary(next, [source, handler])]);
        result.IsValid.Should().Be(!handledFailure, string.Join("; ", result.Errors));
    }
}
