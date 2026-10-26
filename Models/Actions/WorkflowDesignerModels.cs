using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Models.Actions;

public sealed class WorkflowDesignerRequest
{
    public Guid TenantId { get; set; }
    public bool JurisdictionsConfirmed { get; set; }
    public bool EconomicTermsConfirmed { get; set; }
    public bool PlanningScopeAccepted { get; set; }
    [MaxLength(40)] public List<string> ConfirmedResourceHandles { get; set; } = [];
    [Required] public WorkflowDesignerIntake Intake { get; set; } = new();
    [MaxLength(16)] public List<WorkflowDesignerMessage> Messages { get; set; } = [];
    public bool ConsentToProvider { get; set; }
}

public sealed class WorkflowDesignerIntake
{
    [MaxLength(2500)] public string Purpose { get; set; } = "";
    [MaxLength(2500)] public string Participants { get; set; } = "";
    [MaxLength(2500)] public string Jurisdictions { get; set; } = "";
    [MaxLength(2500)] public string ValueMovement { get; set; } = "";
    [MaxLength(2500)] public string AllocationsAndTax { get; set; } = "";
    [MaxLength(2500)] public string FailureAndRefunds { get; set; } = "";
}

public sealed class WorkflowDesignerMessage
{
    [Required, RegularExpression("^(user|assistant)$")] public string Role { get; set; } = "user";
    [Required, StringLength(4000, MinimumLength = 1)] public string Content { get; set; } = "";
}

public sealed record WorkflowDesignerSource(string Id, string Title, string Url, string Jurisdiction);
public sealed record WorkflowDesignerAvailability(bool Available, string Provider, string Model, string Disclosure, Guid? TenantId = null);

public sealed class WorkflowDesignerReply
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WorkflowDesignerState State { get; set; }
    public string Summary { get; set; } = "";
    public List<string> Questions { get; set; } = [];
    public List<string> Concerns { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
    public List<string> ReviewQuestions { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    public bool ReadyForDraft { get; set; }
    public ActionCreateModel? Draft { get; set; }
    public List<string> DraftErrors { get; set; } = [];
    public IReadOnlyList<WorkflowDesignerSource> Sources { get; set; } = [];
}

public enum WorkflowDesignerState { NeedsDetails, NeedsConfirmation, NeedsPlanningScope, NeedsPlanningStructure, DraftRejected, DraftReady }
public enum WorkflowDesignerDimension { Purpose, Participants, Jurisdictions, ValueMovement, AllocationsAndTax, FailureAndRefunds }
public enum WorkflowDesignerQuestion
{
    Purpose, ParticipantRoles, OperatingLocations, ValueAndCustody, AllocationsAndRecords, FailureAndRefunds,
    ApprovalEvidence, ExternalSettlementEvidence, TaxYear, PayerPayeeClassification, PaymentSourceAndResidence,
    LocalProfessionalReview, PlanningSequence
}
public enum WorkflowDesignerRisk { WorkerClassification, IncomeAndValuation, ReportingAndWithholding, CrossBorderFacts, CustodyAndLicensing, SecuritiesAndLending, EligibilityAndPrivacy, ConsumerAndDisputeTerms }
public enum WorkflowDesignerBlocker { FundsMovementUnsupported, AutomaticEnforcementUnsupported, ExternalSettlementRequired, PlanningSequenceUnclear }
public enum WorkflowDesignerStage { RequestReview, EligibilityReview, EvidenceReview, ApprovalReview, DisputeReview, ExternalSettlementReview, ReconciliationReview, CancellationReview, RecordsReview }

public sealed class WorkflowDesignerModelReply
{
    [JsonRequired] public List<WorkflowDesignerQuestion> QuestionIds { get; set; } = [];
    [JsonRequired] public List<WorkflowDesignerRisk> RiskIds { get; set; } = [];
    [JsonRequired] public List<WorkflowDesignerDimension> GapIds { get; set; } = [];
    [JsonRequired] public List<WorkflowDesignerBlocker> BlockerIds { get; set; } = [];
    [JsonRequired] public List<WorkflowDesignerStage> PlanStages { get; set; } = [];
}

public sealed record WorkflowDesignerModelResult(WorkflowDesignerModelReply? Reply, string? Code = null);
