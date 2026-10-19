using System.Net;
using System.Text.Json;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Designer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class WorkflowDesignerTests
{
    private const string ValidSelection = """{"questionIds":[],"riskIds":[],"gapIds":[],"blockerIds":[],"planStages":["ApprovalReview","ExternalSettlementReview","ReconciliationReview"]}""";

    [Fact]
    public async Task NoConsent_NeverCallsModel()
    {
        var model = new FakeModel();
        var result = await Service(model).InterviewAsync(new(), default);
        Assert.Equal("AI_CONSENT_REQUIRED", result.Code);
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Jurisdiction is unknown")]
    [InlineData("not yet determined")]
    [InlineData("we have not decided")]
    public async Task WholeAnswerUncertainty_NeedsDetailsWithoutPretendingADraftExists(string answer)
    {
        var request = CompleteRequest(); request.Intake.Jurisdictions = answer;
        var reply = (await Service(new()).InterviewAsync(request, default)).Result!;
        Assert.Equal(WorkflowDesignerState.NeedsDetails, reply.State);
        Assert.Null(reply.Draft); Assert.False(reply.ReadyForDraft);
        Assert.Contains(reply.Questions, question => question.Contains("tax resident"));
        Assert.Contains("No draft is available", reply.Summary);
    }

    [Fact]
    public async Task CompleteRecoveryWithUnknownPaymentStateAndAmounts_ProducesOnlyCanonicalPlanningData()
    {
        var request = CompleteRequest();
        request.Intake.ValueMovement = "5000 cents per job, 50/50 allocations, fee cap 100 cents; operator owns the funds.";
        request.Intake.FailureAndRefunds = "Two independent officers approve. Paid completes; confirmed unpaid allows one reviewed retry; unknown stays frozen for treasurer investigation. Cancellation returns earmarks and disputes freeze.";
        var model = new FakeModel();
        model.Reply.QuestionIds = [WorkflowDesignerQuestion.TaxYear, WorkflowDesignerQuestion.PayerPayeeClassification, WorkflowDesignerQuestion.PaymentSourceAndResidence];
        model.Reply.RiskIds = [WorkflowDesignerRisk.ReportingAndWithholding, WorkflowDesignerRisk.WorkerClassification];
        var reply = (await Service(model).InterviewAsync(request, default)).Result!;
        Assert.Equal(WorkflowDesignerState.DraftReady, reply.State);
        Assert.True(reply.ReadyForDraft); Assert.NotNull(reply.Draft); Assert.False(reply.Draft.IsPublic);
        Assert.Empty(reply.Questions); Assert.NotEmpty(reply.ReviewQuestions);
        Assert.Contains(reply.ReviewQuestions, question => question.Contains("tax year"));
        Assert.Equal("5000 cents per job, 50/50 allocations, fee cap 100 cents; operator owns the funds.", request.Intake.ValueMovement);
        Assert.All(reply.Draft.Nodes, node => {
            Assert.Equal(ActionNodeType.Emit, node.NodeType);
            using var config = JsonDocument.Parse(node.Config);
            Assert.Equal("planning.checkpoint", config.RootElement.GetProperty("eventType").GetString());
            var payload = config.RootElement.GetProperty("payload");
            Assert.Equal(new[] { "checkpoint", "fundsMoved", "requiresExternalReview", "scope" }, payload.EnumerateObject().Select(p => p.Name).OrderBy(name => name).ToArray());
            Assert.Equal("planning_data_only", payload.GetProperty("scope").GetString());
            Assert.False(payload.GetProperty("fundsMoved").GetBoolean());
            Assert.True(payload.GetProperty("requiresExternalReview").GetBoolean());
            Assert.True(Enum.TryParse<WorkflowDesignerStage>(payload.GetProperty("checkpoint").GetString(), out _));
        });
        Assert.Contains(reply.Limitations, value => value.Contains("automatic fiat payouts"));
        Assert.Contains("No two-person approval", reply.Draft.Description);
        Assert.Contains(WorkflowDesignerCatalog.ExecutionDisclosure, reply.Draft.Description);
        Assert.Contains(WorkflowDesignerCatalog.ExecutionDisclosure, reply.Limitations);
        Assert.Equal(reply.Draft.Nodes.Count - 1, reply.Draft.Edges.Count);
        Assert.Empty(reply.DraftErrors);
        var serialized = JsonSerializer.Serialize(reply);
        foreach (var forbidden in new[] { "$600", "24%", "30%", "JurisdictionsConfirmed", "EconomicTermsConfirmed", "5000", "50/50" }) Assert.DoesNotContain(forbidden, serialized);
    }

    [Fact]
    public async Task PlanningScopeMustBeExplicit_AndFactsDoNotMeanCompliance()
    {
        var request = CompleteRequest(); request.PlanningScopeAccepted = false;
        var reply = (await Service(new()).InterviewAsync(request, default)).Result!;
        Assert.Equal(WorkflowDesignerState.NeedsPlanningScope, reply.State); Assert.Null(reply.Draft); Assert.False(reply.ReadyForDraft);
        request.PlanningScopeAccepted = true; request.EconomicTermsConfirmed = false;
        reply = (await Service(new()).InterviewAsync(request, default)).Result!;
        Assert.Equal(WorkflowDesignerState.NeedsConfirmation, reply.State); Assert.Null(reply.Draft);
        Assert.Contains(reply.Questions, question => question.Contains("economic terms"));
        Assert.DoesNotContain("EconomicTermsConfirmed", JsonSerializer.Serialize(reply));
    }

    [Fact]
    public async Task SpecificOperationalQuestionsSuppressGenericDimensionDuplicates()
    {
        var model = new FakeModel();
        model.Reply.GapIds = [WorkflowDesignerDimension.FailureAndRefunds, WorkflowDesignerDimension.FailureAndRefunds];
        model.Reply.QuestionIds = [WorkflowDesignerQuestion.FailureAndRefunds, WorkflowDesignerQuestion.ApprovalEvidence,
            WorkflowDesignerQuestion.ApprovalEvidence, WorkflowDesignerQuestion.ExternalSettlementEvidence];
        var reply = (await Service(model).InterviewAsync(CompleteRequest(), default)).Result!;
        Assert.Equal(WorkflowDesignerState.NeedsDetails, reply.State); Assert.Null(reply.Draft);
        Assert.Equal(2, reply.Questions.Count);
        Assert.Contains(WorkflowDesignerCatalog.Question(WorkflowDesignerQuestion.ApprovalEvidence), reply.Questions);
        Assert.Contains(WorkflowDesignerCatalog.Question(WorkflowDesignerQuestion.ExternalSettlementEvidence), reply.Questions);
        Assert.DoesNotContain(WorkflowDesignerCatalog.Question(WorkflowDesignerQuestion.FailureAndRefunds), reply.Questions);
    }

    [Fact]
    public async Task UnsupportedAutomationIsDisclosedWithoutBlockingChosenPlanningScope()
    {
        var model = new FakeModel(); model.Reply.BlockerIds = [WorkflowDesignerBlocker.FundsMovementUnsupported];
        var reply = (await Service(model).InterviewAsync(CompleteRequest(), default)).Result!;
        Assert.Equal(WorkflowDesignerState.DraftReady, reply.State); Assert.NotNull(reply.Draft);
        Assert.Contains(reply.Limitations, text => text.Contains("not evidence that a payment occurred"));
        model.Reply.BlockerIds = [WorkflowDesignerBlocker.PlanningSequenceUnclear];
        reply = (await Service(model).InterviewAsync(CompleteRequest(), default)).Result!;
        Assert.Equal(WorkflowDesignerState.NeedsPlanningStructure, reply.State); Assert.Null(reply.Draft);
        Assert.False(reply.ReadyForDraft); Assert.Contains("No draft", reply.Summary);
    }

    [Fact]
    public async Task InvalidOrNullCatalogSelection_IsRefused()
    {
        var model = new FakeModel(); model.Reply.RiskIds = [(WorkflowDesignerRisk)999];
        Assert.Equal("AI_INVALID_REPLY", (await Service(model).InterviewAsync(CompleteRequest(), default)).Code);
        model.Reply.RiskIds = null!;
        Assert.Equal("AI_INVALID_REPLY", (await Service(model).InterviewAsync(CompleteRequest(), default)).Code);
    }

    [Fact]
    public async Task EmptyOrGenericOutline_DoesNotBecomeAnApparentlyReadyDraft()
    {
        var model = new FakeModel(); model.Reply.PlanStages = [];
        var reply = (await Service(model).InterviewAsync(CompleteRequest(), default)).Result!;
        Assert.Equal(WorkflowDesignerState.NeedsPlanningStructure, reply.State);
        Assert.Null(reply.Draft); Assert.False(reply.ReadyForDraft);
        model.Reply.PlanStages = [WorkflowDesignerStage.RequestReview, WorkflowDesignerStage.RecordsReview];
        reply = (await Service(model).InterviewAsync(CompleteRequest(), default)).Result!;
        Assert.Equal(WorkflowDesignerState.NeedsPlanningStructure, reply.State);
        Assert.Null(reply.Draft); Assert.False(reply.ReadyForDraft);
    }

    [Fact]
    public async Task FailedGraphValidation_ClearsProposalAndUsesTruthfulState()
    {
        var service = new WorkflowDesignerService(new FakeModel(), new RejectingDagValidator(), new ActionDagExecutabilityValidator());
        var reply = (await service.InterviewAsync(CompleteRequest(), default)).Result!;
        Assert.Equal(WorkflowDesignerState.DraftRejected, reply.State);
        Assert.Null(reply.Draft); Assert.False(reply.ReadyForDraft);
        Assert.Contains("No draft is available", reply.Summary); Assert.NotEmpty(reply.DraftErrors);
    }

    [Theory]
    [InlineData("{\"questionIds\":[\"1099 threshold $600\"],\"riskIds\":[],\"gapIds\":[],\"blockerIds\":[],\"planStages\":[]}")]
    [InlineData("{\"questionIds\":[],\"riskIds\":[],\"gapIds\":[],\"blockerIds\":[],\"planStages\":[],\"summary\":\"24% backup and 30% nonresident withholding\"}")]
    [InlineData("{\"questionIds\":[],\"riskIds\":[],\"gapIds\":[],\"blockerIds\":[],\"planStages\":[],\"draft\":{\"description\":\"This draft models current law\"}}")]
    [InlineData("{\"questionIds\":[],\"riskIds\":[],\"gapIds\":[],\"blockerIds\":[],\"planStages\":[0]}")]
    [InlineData("{}")]
    public async Task NarrativeAndUnknownOrMissingModelFields_CannotReachTheProduct(string content)
    {
        var result = await Provider(new(content)).InterviewAsync(CompleteRequest(), default);
        Assert.Equal("AI_INVALID_REPLY", result.Code); Assert.Null(result.Reply);
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
    [InlineData("```json\n{}\n```")]
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
        var http = new FakeHttpHandler(ValidSelection);
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
        Assert.True(body.RootElement.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
        Assert.False(body.RootElement.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean());
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var responseSchema = format.GetProperty("json_schema");
        Assert.True(responseSchema.GetProperty("strict").GetBoolean());
        var schema = responseSchema.GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(5, schema.GetProperty("required").GetArrayLength());
        Assert.True(schema.GetRawText().Length < 16000);
        foreach (var field in schema.GetProperty("properties").EnumerateObject())
        {
            Assert.Equal("array", field.Value.GetProperty("type").GetString());
            Assert.Equal("string", field.Value.GetProperty("items").GetProperty("type").GetString());
            Assert.NotEmpty(field.Value.GetProperty("items").GetProperty("enum").EnumerateArray());
        }
        Assert.False(schema.GetProperty("properties").TryGetProperty("summary", out _));
        Assert.False(schema.GetProperty("properties").TryGetProperty("draft", out _));
        Assert.DoesNotContain("test-only-not-a-key", http.Body!);
        Assert.DoesNotContain(request.TenantId.ToString(), http.Body!);
        Assert.DoesNotContain("confirmed-asset-123", http.Body!);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task UnsupportedStructuredOutputOrProviderRefusal_IsUnavailableWithoutRelaxingFormat(HttpStatusCode status)
    {
        var http = new FakeHttpHandler("provider detail must not reach the caller", status: status);
        var result = await Provider(http).InterviewAsync(CompleteRequest(), CancellationToken.None);
        Assert.Equal("AI_PROVIDER_UNAVAILABLE", result.Code);
        Assert.Null(result.Reply);
        Assert.Equal(1, http.Calls);
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

    private sealed class RejectingDagValidator : IActionDagValidator
    {
        /// <inheritdoc/>
        public DagValidationResult Validate(ActionDefinition action, bool fanOutAsError = false)
            => new() { IsValid = false, Errors = ["Synthetic graph-validation refusal."] };
    }
    private static OpenRouterWorkflowDesignerModel Provider(FakeHttpHandler http) => new(new FakeClientFactory(http),
        Options.Create(new WorkflowDesignerOptions { Enabled = true, ApiKey = "test-only-not-a-key" }));
    private static WorkflowDesignerRequest CompleteRequest() => new()
    {
        ConsentToProvider = true, PlanningScopeAccepted = true,
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
        public WorkflowDesignerModelReply Reply { get; } = new() { PlanStages = [WorkflowDesignerStage.ApprovalReview, WorkflowDesignerStage.ExternalSettlementReview, WorkflowDesignerStage.ReconciliationReview] };
        public Task<WorkflowDesignerModelResult> InterviewAsync(WorkflowDesignerRequest request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new WorkflowDesignerModelResult(Reply)); }
    }

    private sealed class FakeClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeHttpHandler(string content, bool rawEnvelope = false, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Uri = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization!.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(status) { Content = new StringContent(rawEnvelope ? content : JsonSerializer.Serialize(new {
                choices = new[] { new { finish_reason = "stop", message = new { content } } }
            })) };
        }
    }
}
