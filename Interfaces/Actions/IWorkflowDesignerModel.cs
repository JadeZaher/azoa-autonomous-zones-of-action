using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Interfaces.Actions;

public interface IWorkflowDesignerModel
{
    /// <summary>Reports configured provider egress without exposing credentials.</summary>
    WorkflowDesignerAvailability Availability { get; }

    /// <summary>Selects closed interview and planning categories without tools or persistence; cancellation propagates and refusals are typed.</summary>
    Task<WorkflowDesignerModelResult> InterviewAsync(WorkflowDesignerRequest request, CancellationToken cancellationToken);
}
