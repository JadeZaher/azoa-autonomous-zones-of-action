using System.Collections.Concurrent;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.TestSupport;

public sealed class RecordingActionWorkflowAdmissionStore : IActionWorkflowAdmissionStore
{
    private readonly ConcurrentDictionary<(Guid Run, Guid Node), string> _steps = new();
    public IReadOnlyCollection<Guid> Nodes => _steps.Keys.Select(key => key.Node).ToArray();

    public Task<AZOAResult<ActionRun>> AdmitAsync(ActionWorkflowAdmission admission, CancellationToken ct = default)
        => throw new NotSupportedException("This fixture records successor admission only.");

    public Task EnqueueNodeAsync(Guid runId, Guid nodeId, string payload, CancellationToken ct = default)
    {
        var saved = _steps.GetOrAdd((runId, nodeId), payload);
        if (saved != payload) throw new InvalidOperationException("Changed immutable successor input.");
        return Task.CompletedTask;
    }
}
