using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Actions.Designer;

public sealed class WorkflowDesignerService(IWorkflowDesignerModel model, IActionDagValidator dagValidator,
    IActionDagExecutabilityValidator executabilityValidator)
{
    public async Task<AZOAResult<WorkflowDesignerReply>> InterviewAsync(WorkflowDesignerRequest request, CancellationToken cancellationToken)
    {
        if (!request.ConsentToProvider)
            return new() { IsError = true, Code = "AI_CONSENT_REQUIRED", Message = "Review and accept the AI data-sharing disclosure before sending your brief." };
        var response = await model.InterviewAsync(request, cancellationToken);
        if (response.Reply is not { } reply)
            return new() { IsError = true, Code = response.Code ?? "AI_UNAVAILABLE", Message = "The workflow assistant is unavailable. Your workflow has not been saved or changed. Try again later or use the visual builder." };
        if (reply.Summary is null || reply.Summary.Length > 4000 || reply.Questions is null || reply.Concerns is null || reply.Assumptions is null ||
            new[] { reply.Questions, reply.Concerns, reply.Assumptions }.Any(list => list.Count > 12 || list.Any(s => s is null || s.Length > 4000)))
            return new() { IsError = true, Code = "AI_INVALID_REPLY", Message = "The assistant returned an invalid reply. Your workflow has not changed." };
        var missing = WorkflowDesignerPolicy.MissingIntake(request.Intake);
        if (!request.JurisdictionsConfirmed) missing.Add("Confirm that you have identified every relevant operator/participant jurisdiction and tax residence, or explain what remains unknown.");
        if (!request.EconomicTermsConfirmed) missing.Add("Confirm that the economic terms, exact units, allocations, approvals and refund/failure paths have been specified, or explain unresolved decisions.");
        reply.Questions = missing.Concat(reply.Questions).Distinct(StringComparer.Ordinal).Take(12).ToList();
        reply.Sources = WorkflowDesignerPolicy.Sources;
        reply.Concerns.Add("Tax: confirm income/disposal treatment, valuation and basis records, withholding/reporting and any sales/VAT/GST obligations for each relevant jurisdiction. The assistant cannot determine your tax liability.");
        reply.Concerns.Add("Legal: confirm the operator's custody/money-transmission role, participant eligibility and approval/refund/dispute terms with advisers in the jurisdictions you identified. A draft is not permission to operate.");
        reply.DraftErrors = [];
        if (!reply.ReadyForDraft || reply.Questions.Count > 0)
        {
            reply.Draft = null;
            reply.ReadyForDraft = false;
        }
        else
        {
            reply.DraftErrors = WorkflowDesignerPolicy.ValidateDraft(reply, request, dagValidator, executabilityValidator);
            if (reply.DraftErrors.Count > 0) { reply.Draft = null; reply.ReadyForDraft = false; }
        }
        return new() { Result = reply };
    }
}
