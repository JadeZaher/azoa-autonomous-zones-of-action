using System.Text.Json;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Services.Deployments;

/// <summary>Reconciles immutable deployment requests without granting new signing authority.</summary>
public sealed class DeploymentActionResolver(IDeploymentService deployments, IDeploymentStore store) : IActionExternalOperationResolver
{
    /// <inheritdoc/>
    public string Domain => "deployment";

    /// <inheritdoc/>
    public async Task<ActionExternalOperationObservation> ObserveAsync(ActionExternalOperationReference reference, string canonicalInput, CancellationToken ct = default)
    {
        if (reference.Version != 1 || reference.Domain != Domain || CanonicalJson.HashCanonical(canonicalInput) != reference.IntentHash)
            return Ambiguous(reference, "Deployment recovery identity is invalid.");
        var frozen = JsonSerializer.Deserialize<ActionFrozenNodeInput>(canonicalInput);
        if (frozen is null || frozen.Version != 1 || frozen.ActorUserId != reference.ActorUserId
            || frozen.ActingTenantId != reference.ActingTenantId || frozen.NodeType != ActionNodeType.ActionGraphBundleDeploy
            || reference.RequestKey != RequestKey(frozen.RunId, frozen.NodeId)
            || reference.OperationId != DeploymentIdentity.Operation(reference.ActorUserId, reference.ActingTenantId, reference.RequestKey))
            return Ambiguous(reference, "Deployment request correlation does not match its frozen action.");
        var config = frozen.Config.Deserialize<DeploymentNodeConfig>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (config?.Deployment is null) return Ambiguous(reference, "Saved typed deployment request is missing.");
        var row = await store.GetAsync(reference.OperationId!, ct);
        if (row is null)
        {
            var admission = await deployments.StartActionAsync(reference, canonicalInput, false, ct);
            if (admission.IsError || admission.Result is null)
                return Ambiguous(reference, admission.Message);
            return Observation(reference, admission.Result);
        }
        var saved = JsonSerializer.Deserialize<DeploymentStartRequest>(row.RequestJson)
            ?? throw new InvalidOperationException("Admitted deployment request is unreadable.");
        var expected = config.Deployment;
        if ((expected.NativeIntent is null) != (saved.NativeIntent is null))
            return Ambiguous(reference, "Saved deployment target intent does not match the frozen request.");
        if (expected.NativeIntent is not null)
            expected = expected with { NativeIntent = expected.NativeIntent with
            {
                OperationKey = saved.NativeIntent!.OperationKey, ProtocolIdentity = saved.NativeIntent.ProtocolIdentity,
                Budget = expected.NativeIntent.Budget with { QuoteId = saved.NativeIntent.Budget.QuoteId }, Quote = saved.NativeIntent.Quote,
            } };
        if (row.ActorUserId != reference.ActorUserId.ToString("N") || row.TenantId != (reference.ActingTenantId?.ToString("N") ?? "")
            || row.ActionRunId != frozen.RunId.ToString("N") || row.ActionNodeId != frozen.NodeId.ToString("N") || row.ActionIntentHash != reference.IntentHash
            || CanonicalJson.Hash(expected) != row.IntentHash)
            return Ambiguous(reference, "Saved deployment belongs to another frozen request.");
        await deployments.ObserveAsync(row.Id, ct);
        var result = await deployments.GetAsync(row.Id, reference.ActorUserId, reference.ActingTenantId, ct);
        return result.IsError || result.Result is null ? Ambiguous(reference, result.Message) : Observation(reference, result.Result);
    }

    public static string RequestKey(Guid run, Guid node) => $"action:{run:N}:{node:N}:deployment";

    public static ActionExternalOperationObservation Observation(ActionExternalOperationReference reference, DeploymentReceipt receipt)
        => new(receipt.State switch
        {
            "Confirmed" => ActionExternalOperationStatus.Succeeded,
            "Cancelled" => ActionExternalOperationStatus.Cancelled,
            "Failed" => ActionExternalOperationStatus.Failed,
            "NeedsOperator" => ActionExternalOperationStatus.Ambiguous,
            _ => ActionExternalOperationStatus.Pending,
        }, reference, JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            receipt.Detail ?? (receipt.State is "Admitted" or "AwaitingAuthorization" ? "Continue the saved deployment to authorize its next step." : null),
            receipt.TransactionIds.LastOrDefault(), receipt.Target.Kind == "NativeContract" ? receipt.Target.ChainType : null);

    private static ActionExternalOperationObservation Ambiguous(ActionExternalOperationReference reference, string detail)
        => new(ActionExternalOperationStatus.Ambiguous, reference, Detail: detail);
}
