using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions.Workflow;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>Persists fixture admissions and successor rows for the real saga processor.</summary>
public sealed class InMemoryActionWorkflowAdmissionStore(IActionRunStore runs, IActionNodeExecutionStore executions,
    ISagaStore sagas) : IActionWorkflowAdmissionStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(Guid Run, Guid Node), string> _steps = [];

    public async Task<AZOAResult<ActionRun>> AdmitAsync(ActionWorkflowAdmission admission, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var prior = await runs.GetByIdAsync(admission.Run.Id, ct);
            if (prior.Result is { } saved)
                return saved.InvocationIdentity == admission.Run.InvocationIdentity
                    ? AZOAResult<ActionRun>.Success(saved) : AZOAResult<ActionRun>.Failure("Conflicting fixture admission.");
            var created = await runs.CreateAsync(admission.Run, ct);
            foreach (var node in admission.Graph.Nodes)
                await executions.CreateAsync(new ActionNodeExecution { Id = Guid.NewGuid(), RunId = admission.Run.Id, NodeId = node.Id }, ct);
            await EnqueueCore(admission.Run.Id, admission.EntryNodeId,
                SagaStep<ActionStepPayload>.Serialize(new(admission.Run.Id, admission.Run.ActionId, admission.Run.UserId, admission.EntryNodeId)), ct);
            return created;
        }
        finally { _gate.Release(); }
    }

    public async Task EnqueueNodeAsync(Guid runId, Guid nodeId, string payload, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await EnqueueCore(runId, nodeId, payload, ct); }
        finally { _gate.Release(); }
    }

    private async Task EnqueueCore(Guid runId, Guid nodeId, string payload, CancellationToken ct)
    {
        if (_steps.TryGetValue((runId, nodeId), out var prior))
        {
            if (prior != payload) throw new InvalidOperationException("Changed fixture successor payload.");
            return;
        }
        await sagas.EnqueueAsync(ActionWorkflowSaga.Name, nodeId.ToString(), runId.ToString(),
            ActionWorkflowIdentity.StepId(runId, nodeId), payload, false, ct);
        _steps.Add((runId, nodeId), payload);
    }
}
