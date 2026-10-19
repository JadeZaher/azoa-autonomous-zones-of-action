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
        if (response.Reply is not { } selection)
            return new() { IsError = true, Code = response.Code ?? "AI_UNAVAILABLE", Message = "The workflow assistant is unavailable. Your workflow has not been saved or changed. Try again later or use the visual builder." };
        if (!ValidIds(selection.QuestionIds, 12) || !ValidIds(selection.RiskIds, 8) || !ValidIds(selection.GapIds, 6)
            || !ValidIds(selection.BlockerIds, 4) || !ValidIds(selection.PlanStages, 9))
            return new() { IsError = true, Code = "AI_INVALID_REPLY", Message = "The assistant returned an invalid checklist. Your workflow has not changed." };

        var questions = selection.QuestionIds.Where(id => WorkflowDesignerCatalog.Dimension(id) is not null).Distinct().ToList();
        var specificDimensions = questions.Where(id => id != WorkflowDesignerCatalog.DefaultQuestion(WorkflowDesignerCatalog.Dimension(id)!.Value))
            .Select(WorkflowDesignerCatalog.Dimension).ToHashSet();
        questions.RemoveAll(id => specificDimensions.Contains(WorkflowDesignerCatalog.Dimension(id))
            && id == WorkflowDesignerCatalog.DefaultQuestion(WorkflowDesignerCatalog.Dimension(id)!.Value));
        foreach (var dimension in WorkflowDesignerPolicy.MissingDimensions(request.Intake).Concat(selection.GapIds).Distinct())
            if (!questions.Any(id => WorkflowDesignerCatalog.Dimension(id) == dimension)) questions.Add(WorkflowDesignerCatalog.DefaultQuestion(dimension));
        var reply = new WorkflowDesignerReply
        {
            Questions = questions.Select(WorkflowDesignerCatalog.Question).ToList(),
            ReviewQuestions = selection.QuestionIds.Where(id => WorkflowDesignerCatalog.Dimension(id) is null)
                .Append(WorkflowDesignerQuestion.TaxYear).Append(WorkflowDesignerQuestion.LocalProfessionalReview)
                .Distinct().Select(WorkflowDesignerCatalog.Question).ToList(),
            Concerns = selection.RiskIds.Append(WorkflowDesignerRisk.ReportingAndWithholding).Append(WorkflowDesignerRisk.CustodyAndLicensing)
                .Distinct().Select(WorkflowDesignerCatalog.Risk).ToList(),
            Limitations = selection.BlockerIds.Concat(new[] { WorkflowDesignerBlocker.FundsMovementUnsupported,
                    WorkflowDesignerBlocker.AutomaticEnforcementUnsupported, WorkflowDesignerBlocker.ExternalSettlementRequired })
                .Distinct().Select(WorkflowDesignerCatalog.Limitation).Append(WorkflowDesignerCatalog.ExecutionDisclosure).ToList(),
            Assumptions = ["Your brief contains user-stated facts, not verified legal or tax classifications.",
                "The planning graph does not embed or enforce your amounts, allocations, timing or approval rules. Preserve those decisions for separate implementation and review."],
            Sources = WorkflowDesignerPolicy.Sources
        };
        if (questions.Count > 0) reply.State = WorkflowDesignerState.NeedsDetails;
        else if (!request.JurisdictionsConfirmed || !request.EconomicTermsConfirmed) reply.State = WorkflowDesignerState.NeedsConfirmation;
        else if (!request.PlanningScopeAccepted) reply.State = WorkflowDesignerState.NeedsPlanningScope;
        else if (!selection.PlanStages.Any(stage => stage is not WorkflowDesignerStage.RequestReview and not WorkflowDesignerStage.RecordsReview)
                 || selection.BlockerIds.Contains(WorkflowDesignerBlocker.PlanningSequenceUnclear))
        {
            reply.State = WorkflowDesignerState.NeedsPlanningStructure;
            reply.Questions.Add(WorkflowDesignerCatalog.Question(WorkflowDesignerQuestion.PlanningSequence));
        }
        else
        {
            reply.Draft = WorkflowDesignerCatalog.PlanningDraft(selection.PlanStages);
            reply.DraftErrors = WorkflowDesignerPolicy.ValidateDraft(reply, request, dagValidator, executabilityValidator);
            reply.State = reply.DraftErrors.Count == 0 ? WorkflowDesignerState.DraftReady : WorkflowDesignerState.DraftRejected;
            if (reply.State == WorkflowDesignerState.DraftRejected) reply.Draft = null;
        }
        if (!request.JurisdictionsConfirmed) reply.Questions.Add("Confirm the operating locations and tax residences stated in your brief; this confirms facts, not compliance.");
        if (!request.EconomicTermsConfirmed) reply.Questions.Add("Confirm the economic terms, allocations, approvals and failure paths stated in your brief; this does not validate their legal treatment.");
        reply.ReadyForDraft = reply.State == WorkflowDesignerState.DraftReady && reply.Draft is not null;
        reply.Summary = WorkflowDesignerCatalog.Summary(reply.State);
        return new() { Result = reply };
    }

    private static bool ValidIds<T>(IReadOnlyCollection<T>? values, int maximum) where T : struct, Enum
        => values is not null && values.Count <= maximum && values.All(value => Enum.IsDefined(value));
}
