using System.Text.Json;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Deployments;

public sealed partial class DeploymentService
{
    private const int MaxOperatorReasonLength = 500;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeploymentReceipt>> ListNeedsOperatorAsync(int limit, CancellationToken ct = default)
        => (await store.ListNeedsOperatorAsync(Math.Clamp(limit, 1, 100), ct)).Select(View).ToArray();

    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentReceipt>> ResolveNeedsOperatorAsync(string operationId, Guid operatorUserId,
        DeploymentOperatorResolution request, CancellationToken ct = default)
    {
        if (request is null || operatorUserId == Guid.Empty || request.Action is not ("Reobserve" or "CloseUnresolvable"))
            return Fail<DeploymentReceipt>("An explicit operator resolution action is required.");
        var row = await store.GetAsync(operationId, ct);
        if (row is null) return Fail<DeploymentReceipt>("Deployment not found.");
        if (row.State != "NeedsOperator") return Fail<DeploymentReceipt>("Only a deployment parked for operator investigation can be resolved.");
        if (row.Version != request.ExpectedVersion) return Fail<DeploymentReceipt>("The deployment changed; reload it before resolving.");
        // A definitive chain verdict always wins over operator judgement; see Services/Deployments/AGENTS.md §Operator resolution.
        await AdvanceAsync(row, Guid.Empty, null, false, ct);
        var current = await RequireCurrentAsync(row.Id, ct);
        if (request.Action == "Reobserve" || current.State != "NeedsOperator") return AZOAResult<DeploymentReceipt>.Success(View(current));
        // RV-L4-05: a terminal Failed would let the owning action node take its failure/compensation edge while the wire can still land.
        if (current.ActionRunId is not null)
            return Fail<DeploymentReceipt>("An action-linked deployment cannot be operator-closed; it stays parked for re-observation and the action's reconciliation.");
        var reason = request.Reason?.Trim();
        if (!request.AcknowledgeNoFurtherEffect || string.IsNullOrEmpty(reason) || reason.Length > MaxOperatorReasonLength || reason.Any(char.IsControl))
            return Fail<DeploymentReceipt>("Closing requires a bounded reason and an explicit acknowledgement that the unresolved wire can have no further effect.");
        var unresolved = current.PreparedJson;
        current.State = "Failed";
        current.Stage = "OperatorClosed";
        current.SubmissionAuthorized = false;
        current.PreparedJson = null;
        current.EvidenceJson = JsonSerializer.Serialize(new
        {
            kind = "OperatorClosedUnresolvable", operatorUserId = operatorUserId.ToString("N"), reason,
            closedAt = DateTimeOffset.UtcNow, unresolvedPreparation = unresolved,
        });
        current.Detail = "Closed by the node operator after an unresolvable observation: " + reason;
        var saved = await store.UpdateAsync(current, current.Version, ct);
        return saved is null ? Fail<DeploymentReceipt>("The deployment changed while closing; reload it before resolving.")
            : AZOAResult<DeploymentReceipt>.Success(View(saved));
    }
}
