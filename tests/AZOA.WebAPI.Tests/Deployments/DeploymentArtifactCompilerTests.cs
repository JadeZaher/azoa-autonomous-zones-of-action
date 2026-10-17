using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Deployments;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Deployments;

public sealed class DeploymentArtifactCompilerTests
{
    private readonly DeploymentArtifactCompiler _compiler = new(new ActionDagValidator());

    [Fact]
    public void RepeatedActionOccurrencesHaveDistinctNodesAndLocalOutputBindings()
    {
        var action = Graph();
        var sources = new[] { Source(action, "0"), Source(action, "1") };
        var result = _compiler.Compose(Guid.NewGuid(), action.UserId, sources, [], []);
        result.Graph.Nodes.Select(node => node.Id).Should().OnlyHaveUniqueItems();
        result.Graph.Nodes.Should().HaveCount(4);
        result.Graph.Nodes.Single(node => node.Name == "o0_Use").Config.Should().Contain("run.o0_Read.value");
        result.Graph.Nodes.Single(node => node.Name == "o1_Use").Config.Should().Contain("run.o1_Read.value");
        result.Graph.Edges.Single(edge => edge.Condition == "run.o1_Read.flag").SourceNodeId
            .Should().Be(result.Graph.Nodes.Single(node => node.Name == "o1_Read").Id);
        result.Graph.Nodes.Count(node => node.IsEntry).Should().Be(2);
        result.Graph.Nodes.Count(node => node.IsTerminal).Should().Be(2);
        result.OccurrenceBoundaries.Should().ContainSingle();
    }

    [Fact]
    public void InternalDependencyRetainsExactPriorNodeAndRepeatedSourceIsRejected()
    {
        var prior = Graph();
        var next = Graph();
        next.Dependencies.Add(new ActionDependency
        {
            Id = Guid.NewGuid(), ActionId = next.Id, DependsOnActionId = prior.Id,
            DependsOnNodeId = prior.Nodes[0].Id, DependencyType = ActionDependencyType.Required,
        });
        var result = _compiler.Compose(Guid.NewGuid(), prior.UserId, [Source(prior, "0"), Source(next, "1")], [], []);
        result.InternalDependencies.Should().ContainSingle();
        result.InternalDependencies![0].RequiredNodeIds.Should().Equal(result.Graph.Nodes.Single(node => node.Name == "o0_Read").Id);
        var ambiguous = () => _compiler.Compose(Guid.NewGuid(), prior.UserId,
            [Source(prior, "0"), Source(prior, "1"), Source(next, "2")], [], []);
        ambiguous.Should().Throw<ArgumentException>().WithMessage("*exactly one prior*");
    }

    [Fact]
    public void MappingTargetsAnExactRepeatedOccurrenceAndRejectsFutureOutput()
    {
        var action = Graph();
        var mapping = new DeploymentInputMapping("0", action.Nodes[0].Id, "result", "1", action.Nodes[1].Id, "external");
        var result = _compiler.Compose(Guid.NewGuid(), action.UserId, [Source(action, "0"), Source(action, "1")], [], [mapping]);
        using var config = JsonDocument.Parse(result.Graph.Nodes.Single(node => node.Name == "o1_Use").Config);
        config.RootElement.GetProperty("external").GetProperty("$from").GetString().Should().Be("run.o0_Read.result");
        var reverse = mapping with { SourceOccurrence = "1", TargetOccurrence = "0" };
        var invalid = () => _compiler.Compose(Guid.NewGuid(), action.UserId, [Source(action, "0"), Source(action, "1")], [], [reverse]);
        invalid.Should().Throw<ArgumentException>().WithMessage("*prior occurrence*");
    }

    [Fact]
    public void RepeatedNestedBundleDependenciesStayWithinTheirOccurrenceScope()
    {
        var prior = Graph(); var next = Graph();
        next.Dependencies.Add(new ActionDependency { Id = Guid.NewGuid(), ActionId = next.Id,
            DependsOnActionId = prior.Id, DependsOnNodeId = prior.Nodes[0].Id, DependencyType = ActionDependencyType.Required });
        var result = _compiler.Compose(Guid.NewGuid(), prior.UserId,
            [Source(prior, "first/0"), Source(next, "first/1"), Source(prior, "second/0"), Source(next, "second/1")], [], []);
        result.InternalDependencies.Should().HaveCount(2);
        result.InternalDependencies![0].SourceOccurrence.Should().Be("first/0");
        result.InternalDependencies[1].SourceOccurrence.Should().Be("second/0");
        result.InternalDependencies[1].RequiredNodeIds.Should().Equal(result.Graph.Nodes.Single(node => node.Name == "o2_Read").Id);
    }

    [Fact]
    public void LoaderIdentityIsNoncircularAndTargetOrGraphChangesInvalidateArtifact()
    {
        var action = Graph();
        var bundle = Guid.NewGuid();
        var runtime = _compiler.Compose(bundle, action.UserId, [Source(action, "0")], [], []);
        var payload = new DeploymentArtifactPayload(2, bundle, action.UserId, null,
            new DeploymentTarget("PlatformRuntime", "Algorand", "Devnet"), runtime, null);
        var first = DeploymentArtifactCompiler.Compile(payload);
        var repeat = DeploymentArtifactCompiler.Compile(payload);
        first.Should().Be(repeat);
        first.Loader.Should().Contain("deploymentId").And.Contain(first.PayloadHash).And.Contain("fetch(");
        first.Loader.Should().NotContain(first.ArtifactHash);
        first.ArtifactHash.Should().Be(CanonicalJson.Hash(new
        {
            version = 2, payloadHash = first.PayloadHash, loaderHash = CanonicalJson.HashCanonical(first.Loader!),
        }));
        DeploymentArtifactCompiler.Compile(payload with { Target = payload.Target with { Network = "Testnet" } })
            .ArtifactHash.Should().NotBe(first.ArtifactHash);
    }

    [Fact]
    public void ConditionalAndGatePathsRewriteWithoutChangingQuotedLiterals()
    {
        var action = Graph();
        action.Edges[0].Condition = "run.Read.value == 'run.Read.value' && upstream.Read.text == \"upstream.Read.text\"";
        action.Nodes[1].NodeType = ActionNodeType.GateCheck;
        action.Nodes[1].Config = JsonSerializer.Serialize(new { predicate = "run.Read.value == 'it\\\'s run.Read.value'" });
        var compiled = _compiler.Compose(Guid.NewGuid(), action.UserId, [Source(action, "0")], [], []);
        compiled.Graph.Edges[0].Condition.Should().Be("run.o0_Read.value == 'run.Read.value' && upstream.o0_Read.text == \"upstream.Read.text\"");
        using var config = JsonDocument.Parse(compiled.Graph.Nodes.Single(node => node.Name == "o0_Use").Config);
        config.RootElement.GetProperty("predicate").GetString().Should().Be("run.o0_Read.value == 'it\\\'s run.Read.value'");
    }

    [Fact]
    public void ParallelTerminalBranchesJoinBeforeTheNextOccurrence()
    {
        var prior = Graph(); var next = Graph();
        var branch = new ActionNode { Id = Guid.NewGuid(), ActionId = prior.Id, Name = "Other", NodeType = ActionNodeType.HolonGet, IsTerminal = true };
        prior.Nodes.Add(branch);
        prior.Edges.Add(new ActionEdge { Id = Guid.NewGuid(), ActionId = prior.Id, SourceNodeId = prior.Nodes[0].Id, TargetNodeId = branch.Id, EdgeType = ActionEdgeType.Control });
        var compiled = _compiler.Compose(Guid.NewGuid(), prior.UserId, [Source(prior, "0"), Source(next, "1")], [], []);
        var entry = compiled.Graph.Nodes.Single(node => node.Name == "o1_Read");
        compiled.Graph.Edges.Where(edge => edge.TargetNodeId == entry.Id).Should().BeEmpty();
        compiled.OccurrenceBoundaries!.Single(boundary => boundary.EntryNodeId == entry.Id).RequiredNodeIds.Should().BeEquivalentTo(
            compiled.Graph.Nodes.Where(node => node.Name.StartsWith("o0_", StringComparison.Ordinal)).Select(node => node.Id));
    }

    [Fact]
    public void CompetingExplicitMappingsNeverSilentlyOverwriteAnInput()
    {
        var action = Graph();
        var mapping = new DeploymentInputMapping("0", action.Nodes[0].Id, "first", "1", action.Nodes[1].Id, "value");
        var compile = () => _compiler.Compose(Guid.NewGuid(), action.UserId, [Source(action, "0"), Source(action, "1")], [],
            [mapping, mapping with { SourceField = "second" }]);
        compile.Should().Throw<ArgumentException>().WithMessage("*Competing input mappings*");
    }

    private static DeploymentSource Source(ActionDefinition action, string occurrence)
        => new(action.Id, occurrence, action.Version, ActionPublishedVersion.ComputeHash(action), action);

    private static ActionDefinition Graph()
    {
        var action = new ActionDefinition { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Status = ActionStatus.Active };
        var first = new ActionNode { Id = Guid.NewGuid(), ActionId = action.Id, Name = "Read", NodeType = ActionNodeType.HolonGet, IsEntry = true };
        var last = new ActionNode { Id = Guid.NewGuid(), ActionId = action.Id, Name = "Use", NodeType = ActionNodeType.HolonGet, IsTerminal = true,
            Config = "{\"value\":{\"$from\":\"run.Read.value\"}}" };
        action.Nodes.AddRange([first, last]);
        action.Edges.Add(new ActionEdge { Id = Guid.NewGuid(), ActionId = action.Id, SourceNodeId = first.Id,
            TargetNodeId = last.Id, EdgeType = ActionEdgeType.Control, Condition = "run.Read.flag" });
        return action;
    }
}
