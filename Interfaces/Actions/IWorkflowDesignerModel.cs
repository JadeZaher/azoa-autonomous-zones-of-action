using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Interfaces.Actions;

public interface IWorkflowDesignerModel
{
    /// <summary>Reports configured provider egress without exposing credentials.</summary>
    WorkflowDesignerAvailability Availability { get; }

    /// <summary>Interviews the submitted tenant brief without tools or persistence; cancellation propagates and provider refusals are typed.</summary>
    Task<WorkflowDesignerModelResult> InterviewAsync(WorkflowDesignerRequest request, CancellationToken cancellationToken);
}
