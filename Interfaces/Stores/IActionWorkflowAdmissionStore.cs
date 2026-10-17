using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Atomically fences current source access/quota and creates the frozen run, nodes and first saga step.</summary>
public interface IActionWorkflowAdmissionStore
{
    /// <summary>Returns the exact admitted run on replay or refuses changed identity/current source authority.</summary>
    Task<AZOAResult<ActionRun>> AdmitAsync(ActionWorkflowAdmission admission, CancellationToken ct = default);

    /// <summary>Admits one exact action-run/node saga step; duplicate immutable payloads are idempotent.</summary>
    Task EnqueueNodeAsync(Guid runId, Guid nodeId, string payload, CancellationToken ct = default);
}
