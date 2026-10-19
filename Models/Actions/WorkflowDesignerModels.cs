using System.ComponentModel.DataAnnotations;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Models.Actions;

public sealed class WorkflowDesignerRequest
{
    public Guid TenantId { get; set; }
    public bool JurisdictionsConfirmed { get; set; }
    public bool EconomicTermsConfirmed { get; set; }
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
    public string Summary { get; set; } = "";
    public List<string> Questions { get; set; } = [];
    public List<string> Concerns { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
    public bool ReadyForDraft { get; set; }
    public ActionCreateModel? Draft { get; set; }
    public List<string> DraftErrors { get; set; } = [];
    public IReadOnlyList<WorkflowDesignerSource> Sources { get; set; } = [];
}

public sealed record WorkflowDesignerModelResult(WorkflowDesignerReply? Reply, string? Code = null);
