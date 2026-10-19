using System.Net;
using System.Text.Json;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Designer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class WorkflowDesignerTests
{
    [Fact]
    public async Task NoConsent_NeverCallsModel()
    {
        var model = new FakeModel();
        var result = await Service(model).InterviewAsync(new WorkflowDesignerRequest(), CancellationToken.None);
        Assert.Equal("AI_CONSENT_REQUIRED", result.Code);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task MissingJurisdiction_RemovesPrematureModelDraft()
    {
        var request = CompleteRequest();
        request.Intake.Jurisdictions = "unknown";
        var result = await Service(new FakeModel()).InterviewAsync(request, CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Null(result.Result!.Draft);
        Assert.False(result.Result.ReadyForDraft);
        Assert.Contains(result.Result.Questions, q => q.Contains("tax resident"));
        Assert.Contains(result.Result.Concerns, q => q.StartsWith("Tax:"));
        Assert.All(result.Result.Sources, source => Assert.Contains("United States", source.Jurisdiction));
    }

    [Fact]
    public async Task OutstandingFollowUp_RequiresAnotherInterviewTurn()
    {
        var model = new FakeModel();
        model.Reply.Questions.Add("Who authorizes a disputed refund?");
        var result = await Service(model).InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.Null(result.Result!.Draft);
        Assert.False(result.Result.ReadyForDraft);
    }

    [Theory]
    [InlineData("Jurisdiction is unknown")]
    [InlineData("not yet determined")]
    [InlineData("we have not decided")]
    public async Task ExplicitUncertainty_RefusesPrematureDraft(string jurisdiction)
    {
        var request = CompleteRequest();
        request.Intake.Jurisdictions = jurisdiction;
        var result = await Service(new FakeModel()).InterviewAsync(request, CancellationToken.None);
        Assert.Null(result.Result!.Draft);
        Assert.Contains(result.Result.Questions, q => q.Contains("tax resident"));
    }

    [Fact]
    public async Task UnconfirmedEconomicTerms_RefuseDraft()
    {
        var request = CompleteRequest();
        request.EconomicTermsConfirmed = false;
        var result = await Service(new FakeModel()).InterviewAsync(request, CancellationToken.None);
        Assert.Null(result.Result!.Draft);
        Assert.Contains(result.Result.Questions, q => q.Contains("economic terms"));
    }

    [Theory]
    [InlineData("1eec296c664b447597dec274bd36bebe")]
    [InlineData("1234567890123456789")]
    [InlineData("opaque-provider-quote-123")]
    public async Task UnconfirmedResourceForms_RefuseDraft(string identifier)
    {
        var model = new FakeModel();
        model.Reply.Draft!.Nodes[0].Config = JsonSerializer.Serialize(new { eventType = "review", payload = new { quoteId = identifier } });
        var result = await Service(model).InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.Null(result.Result!.Draft);
        Assert.Contains(result.Result.DraftErrors, e => e.Contains("unconfirmed identifier"));
    }

    [Fact]
    public async Task ConfirmedGuidHandles_AcceptCanonicalEquivalentForms()
    {
        var model = new FakeModel();
        model.Reply.Draft!.Nodes[0].Config = """{"eventType":"review","payload":{"recipientId":"1eec296c664b447597dec274bd36bebe"}}""";
        var request = CompleteRequest();
        request.ConfirmedResourceHandles.Add("1eec296c-664b-4475-97de-c274bd36bebe");
        var result = await Service(model).InterviewAsync(request, CancellationToken.None);
        Assert.NotNull(result.Result!.Draft);
    }

    [Theory]
    [InlineData(ActionNodeType.Transfer, "{\"nftId\":\"1eec296c664b447597dec274bd36bebe\",\"request\":{}}")]
    [InlineData(ActionNodeType.Hold, "{\"reservePartition\":\"invented-reserve\",\"assetId\":\"12345\",\"amount\":\"500\",\"autoRelease\":false}")]
    [InlineData(ActionNodeType.LoanOpen, "{\"principal\":\"500\",\"currencyCode\":\"unknown-asset\"}")]
    [InlineData(ActionNodeType.Emit, "{\"eventType\":\"review\",\"payload\":{\"source\":{\"$from\":\"holon.1eec296c664b447597dec274bd36bebe.status\"}}}")]
    public async Task ActualEconomicConfigAndHolonBindings_RejectUnconfirmedResources(ActionNodeType type, string config)
    {
        var model = new FakeModel();
        model.Reply.Draft!.Nodes[0].NodeType = type;
        model.Reply.Draft.Nodes[0].Config = config;
        var result = await Service(model).InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.Null(result.Result!.Draft);
        Assert.Contains(result.Result.DraftErrors, error => error.Contains("unconfirmed identifier") || error.Contains("config parse error"));
    }

    [Fact]
    public async Task HoldNodeName_MustReferenceAnActualHoldProducer()
    {
        var model = new FakeModel();
        model.Reply.Draft!.Nodes[0].NodeType = ActionNodeType.Release;
        model.Reply.Draft.Nodes[0].Config = """{"holdNodeName":"inventedHold"}""";
        var result = await Service(model).InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.Null(result.Result!.Draft);
        Assert.Contains(result.Result.DraftErrors, error => error.Contains("HoldNodeName"));
    }

    [Fact]
    public async Task ValidDraft_IsPrivate_AndNeverSaved()
    {
        var result = await Service(new FakeModel()).InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.True(result.Result!.ReadyForDraft);
        Assert.False(result.Result.Draft!.IsPublic);
        Assert.Empty(result.Result.DraftErrors);
    }

    [Theory]
    [InlineData(ActionNodeType.BlockchainExecute)]
    [InlineData(ActionNodeType.GovernanceVote)]
    [InlineData((ActionNodeType)999)]
    public async Task UnsupportedNode_RefusesDraft(ActionNodeType type)
    {
        var model = new FakeModel();
        model.Reply.Draft!.Nodes[0].NodeType = type;
        var result = await Service(model).InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.Null(result.Result!.Draft);
        Assert.Contains(result.Result.DraftErrors, e => e.Contains("unsupported node"));
    }

    [Fact]
    public async Task InventedHandle_RefusesDraft()
    {
        var model = new FakeModel();
        model.Reply.Draft!.Nodes[0].Config = """{"eventType":"review","payload":{"recipient":"1eec296c-664b-4475-97de-c274bd36bebe"}}""";
        var result = await Service(model).InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.Null(result.Result!.Draft);
        Assert.Contains(result.Result.DraftErrors, e => e.Contains("identifier"));
    }

    [Fact]
    public async Task BadEdgeAndUnknownConfig_RefuseDraft()
    {
        var model = new FakeModel();
        model.Reply.Draft!.Nodes[0].Config = """{"script":"sendFunds()"}""";
        model.Reply.Draft.Edges.Add(new() { SourceNodeId = 0, TargetNodeId = 40 });
        var result = await Service(model).InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.Null(result.Result!.Draft);
        Assert.Contains(result.Result.DraftErrors, e => e.Contains("invalid edge"));
        Assert.Contains(result.Result.DraftErrors, e => e.Contains("config parse error"));
    }

    [Theory]
    [InlineData(ActionNodeType.Emit, "")]
    [InlineData(ActionNodeType.ComposeOutputs, " ")]
    [InlineData(ActionNodeType.ComposeOutputs, "not JSON")]
    [InlineData(ActionNodeType.ComposeOutputs, "{broken")]
    [InlineData(ActionNodeType.ComposeOutputs, "[]")]
    [InlineData(ActionNodeType.ComposeOutputs, "null")]
    public async Task EmptyNodeConfig_IsARefusalRatherThanAnException(ActionNodeType type, string config)
    {
        var model = new FakeModel();
        model.Reply.Draft!.Nodes[0].NodeType = type;
        model.Reply.Draft.Nodes[0].Config = config;
        var result = await Service(model).InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.Null(result.Result!.Draft);
        Assert.NotEmpty(result.Result.DraftErrors);
    }

    [Fact]
    public async Task CyclicGraph_RefusesDraft()
    {
        var model = new FakeModel();
        model.Reply.Draft!.Edges.Add(new() { SourceNodeId = 0, TargetNodeId = 0 });
        var result = await Service(model).InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.Null(result.Result!.Draft);
        Assert.NotEmpty(result.Result.DraftErrors);
    }

    [Fact]
    public async Task NullModelCollections_RefuseReply()
    {
        var model = new FakeModel();
        model.Reply.Questions = null!;
        var result = await Service(model).InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.Equal("AI_INVALID_REPLY", result.Code);
    }

    [Fact]
    public void Endpoint_UsesAuthoringPolicyAndDedicatedBudget_WithoutChildOptIn()
    {
        var controller = typeof(ActionDesignerController);
        Assert.Equal("DappDevelop", Assert.Single(controller.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>()).Policy);
        var method = controller.GetMethod(nameof(ActionDesignerController.Interview))!;
        Assert.Equal("workflow-designer", Assert.Single(method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), true).Cast<EnableRateLimitingAttribute>()).PolicyName);
        Assert.DoesNotContain(controller.GetCustomAttributes(true).Concat(method.GetCustomAttributes(true)), a => a.GetType().Name == "DelegatedChildCredentialAttribute");
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("{\"readyForDraft\":true,\"draft\":{\"nodes\":[{\"nodeType\":\"ExecuteScript\"}]}}")]
    [InlineData("{\"readyForDraft\":true,\"draft\":{\"nodes\":[{\"nodeType\":0}]}}")]
    public async Task ProviderMalformedOrUnknownEnum_IsTypedRefusal(string content)
    {
        var provider = Provider(new FakeHttpHandler(content));
        var result = await provider.InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.Equal("AI_INVALID_REPLY", result.Code);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"choices\":[null]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":{},\"message\":{}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":[]}]}")]
    public async Task ProviderWrongEnvelopeKinds_AreTypedRefusals(string envelope)
    {
        var result = await Provider(new FakeHttpHandler(envelope, rawEnvelope: true)).InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.Equal("AI_INVALID_REPLY", result.Code);
    }

    [Fact]
    public async Task ProviderRequest_ContainsNoTools_AndUsesOnlyServerCredential()
    {
        var http = new FakeHttpHandler("""{"summary":"Which jurisdiction?","questions":["Where?"],"concerns":[],"assumptions":[],"readyForDraft":false,"draft":null}""");
        var request = CompleteRequest();
        request.ConfirmedResourceHandles = ["confirmed-asset-123"];
        var result = await Provider(http).InterviewAsync(request, CancellationToken.None);
        Assert.NotNull(result.Reply);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", http.Uri);
        Assert.Equal("Bearer test-only-not-a-key", http.Authorization);
        using var body = JsonDocument.Parse(http.Body!);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
        Assert.Equal(4000, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("deny", body.RootElement.GetProperty("provider").GetProperty("data_collection").GetString());
        Assert.DoesNotContain("test-only-not-a-key", http.Body!);
        Assert.DoesNotContain(request.TenantId.ToString(), http.Body!);
        Assert.Contains("confirmed-asset-123", http.Body!);
    }

    [Fact]
    public async Task DisabledProvider_MakesNoHttpCall()
    {
        var http = new FakeHttpHandler("{}");
        var provider = new OpenRouterWorkflowDesignerModel(new FakeClientFactory(http), Options.Create(new WorkflowDesignerOptions()));
        Assert.Equal("AI_UNAVAILABLE", (await provider.InterviewAsync(CompleteRequest(), CancellationToken.None)).Code);
        Assert.Null(http.Uri);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(new FakeHttpHandler("{}")).InterviewAsync(CompleteRequest(), cancelled.Token));
    }

    private static WorkflowDesignerService Service(FakeModel model) => new(model, new ActionDagValidator(), new ActionDagExecutabilityValidator());
    private static OpenRouterWorkflowDesignerModel Provider(FakeHttpHandler http) => new(new FakeClientFactory(http),
        Options.Create(new WorkflowDesignerOptions { Enabled = true, ApiKey = "test-only-not-a-key" }));
    private static WorkflowDesignerRequest CompleteRequest() => new()
    {
        ConsentToProvider = true,
        TenantId = Guid.NewGuid(), JurisdictionsConfirmed = true, EconomicTermsConfirmed = true,
        Intake = new() {
            Purpose = "A completed project produces a review notification.", Participants = "Adult cooperative members and a review committee.",
            Jurisdictions = "US Colorado company and Colorado tax-resident members.", ValueMovement = "No payment in this notification-only draft.",
            AllocationsAndTax = "No allocation in this graph; recordkeeping assessed by local advisers.",
            FailureAndRefunds = "Committee reviews the notification before any separate settlement." }
    };

    private sealed class FakeModel : IWorkflowDesignerModel
    {
        public int Calls { get; private set; }
        public WorkflowDesignerAvailability Availability => new(true, "Test", "Test", "Test disclosure");
        public WorkflowDesignerReply Reply { get; } = new() {
            Summary = "Review notification", ReadyForDraft = true,
            Draft = new ActionCreateModel { Name = "Review draft", IsPublic = true,
                Nodes = [new() { Name = "Notify", NodeType = ActionNodeType.Emit,
                    Config = """{"eventType":"review","payload":{}}""", IsEntry = true, IsTerminal = true }] }
        };
        public Task<WorkflowDesignerModelResult> InterviewAsync(WorkflowDesignerRequest request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new WorkflowDesignerModelResult(Reply)); }
    }

    private sealed class FakeClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeHttpHandler(string content, bool rawEnvelope = false) : HttpMessageHandler
    {
        public string? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization!.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(rawEnvelope ? content : JsonSerializer.Serialize(new {
                choices = new[] { new { finish_reason = "stop", message = new { content } } }
            })) };
        }
    }
}
