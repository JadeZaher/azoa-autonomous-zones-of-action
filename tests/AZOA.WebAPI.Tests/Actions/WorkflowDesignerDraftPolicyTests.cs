using System.Text.Json;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Designer;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class WorkflowDesignerDraftPolicyTests
{
    [Theory]
    [InlineData("1eec296c664b447597dec274bd36bebe")]
    [InlineData("1234567890123456789")]
    [InlineData("opaque-provider-quote-123")]
    public void UnconfirmedIdentifierFormsAreRejected(string identifier)
    {
        var draft = Graph();
        draft.Nodes[0].Config = JsonSerializer.Serialize(new { eventType = "review", payload = new { quoteId = identifier } });
        Assert.Contains(Validate(draft), error => error.Contains("unconfirmed identifier"));
    }

    [Fact]
    public void ConfirmedGuidHandlesAcceptEquivalentForms_AndDraftIsPrivate()
    {
        var draft = Graph();
        draft.Nodes[0].Config = """{"eventType":"review","payload":{"recipientId":"1eec296c664b447597dec274bd36bebe"}}""";
        Assert.Empty(Validate(draft, ["1eec296c-664b-4475-97de-c274bd36bebe"]));
        Assert.False(draft.IsPublic);
    }

    [Theory]
    [InlineData(ActionNodeType.Transfer, "{\"nftId\":\"1eec296c664b447597dec274bd36bebe\",\"request\":{}}")]
    [InlineData(ActionNodeType.Hold, "{\"reservePartition\":\"invented-reserve\",\"assetId\":\"12345\",\"amount\":\"500\",\"autoRelease\":false}")]
    [InlineData(ActionNodeType.LoanOpen, "{\"principal\":\"500\",\"currencyCode\":\"unknown-asset\"}")]
    [InlineData(ActionNodeType.Emit, "{\"eventType\":\"review\",\"payload\":{\"source\":{\"$from\":\"holon.1eec296c664b447597dec274bd36bebe.status\"}}}")]
    public void EconomicConfigAndHolonBindingsRetainResourceValidation(ActionNodeType type, string config)
    {
        var draft = Graph(); draft.Nodes[0].NodeType = type; draft.Nodes[0].Config = config;
        Assert.Contains(Validate(draft), error => error.Contains("unconfirmed identifier") || error.Contains("config parse error"));
    }

    [Fact]
    public void HoldReferenceMustNameAnActualHoldProducer()
    {
        var draft = Graph(); draft.Nodes[0].NodeType = ActionNodeType.Release;
        draft.Nodes[0].Config = """{"holdNodeName":"inventedHold"}""";
        Assert.Contains(Validate(draft), error => error.Contains("HoldNodeName"));
    }

    [Theory]
    [InlineData(ActionNodeType.BlockchainExecute)]
    [InlineData(ActionNodeType.GovernanceVote)]
    [InlineData((ActionNodeType)999)]
    public void UnsupportedNodesRemainRefused(ActionNodeType type)
    {
        var draft = Graph(); draft.Nodes[0].NodeType = type;
        Assert.Contains(Validate(draft), error => error.Contains("unsupported node"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not JSON")]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    public void EmptyOrMalformedConfigIsRefused(string config)
    {
        var draft = Graph(); draft.Nodes[0].Config = config;
        Assert.NotEmpty(Validate(draft));
    }

    [Fact]
    public void InvalidConfigAndEdgeAreBothReported()
    {
        var draft = Graph(); draft.Nodes[0].Config = """{"script":"sendFunds()"}""";
        draft.Edges.Add(new() { SourceNodeId = 0, TargetNodeId = 40 });
        var errors = Validate(draft);
        Assert.Contains(errors, error => error.Contains("config parse error"));
        Assert.Contains(errors, error => error.Contains("invalid edge"));
    }

    [Fact]
    public void CyclicGraphRemainsRefused()
    {
        var draft = Graph(); draft.Edges.Add(new() { SourceNodeId = 0, TargetNodeId = 0 });
        Assert.NotEmpty(Validate(draft));
    }

    private static ActionCreateModel Graph() => new()
    {
        Name = "Review draft", IsPublic = true, Nodes = [new() { Name = "Notify", NodeType = ActionNodeType.Emit,
            Config = """{"eventType":"review","payload":{}}""", IsEntry = true, IsTerminal = true }]
    };

    private static List<string> Validate(ActionCreateModel draft, List<string>? handles = null)
        => WorkflowDesignerPolicy.ValidateDraft(new() { Draft = draft }, new() { ConfirmedResourceHandles = handles ?? [] },
            new ActionDagValidator(), new ActionDagExecutabilityValidator());
}
