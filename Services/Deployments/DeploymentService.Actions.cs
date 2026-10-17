using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Deployments;

public sealed partial class DeploymentService
{
    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentReceipt>> StartActionAsync(ActionExternalOperationReference reference,
        string canonicalInput, bool authorize, CancellationToken ct = default)
    {
        if (reference.Version != 1 || reference.Domain != "deployment" || reference.ActorUserId == Guid.Empty
            || CanonicalJson.HashCanonical(canonicalInput) != reference.IntentHash)
            return Fail<DeploymentReceipt>("Invalid action deployment authority.");
        var frozen = Read<ActionFrozenNodeInput>(canonicalInput);
        if (frozen.Version != 1 || frozen.RunId == Guid.Empty || frozen.NodeId == Guid.Empty
            || frozen.ActorUserId != reference.ActorUserId || frozen.ActingTenantId != reference.ActingTenantId
            || frozen.NodeType != ActionNodeType.ActionGraphBundleDeploy
            || reference.RequestKey != DeploymentActionResolver.RequestKey(frozen.RunId, frozen.NodeId)
            || reference.OperationId != DeploymentIdentity.Operation(frozen.ActorUserId, frozen.ActingTenantId, reference.RequestKey))
            return Fail<DeploymentReceipt>("Action deployment does not match the frozen run and node.");
        var config = frozen.Config.Deserialize<DeploymentNodeConfig>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (config?.Deployment is not { } request) return Fail<DeploymentReceipt>("A typed action deployment request is required.");
        if (request.NativeIntent is { } intent)
        {
            if (intent.Version != 1 || intent.ArtifactHash != request.ArtifactHash || intent.ChainType != request.Target.ChainType
                || intent.Network != request.Target.Network || request.Target.Kind != "NativeContract"
                || !string.IsNullOrEmpty(intent.OperationKey) && intent.OperationKey != reference.OperationId)
                return Fail<DeploymentReceipt>("Native action identity differs from the exact frozen artifact and target.");
            var quoted = await QuoteCoreAsync(reference.ActorUserId, reference.ActingTenantId,
                new DeploymentQuoteRequest(request.ArtifactId, reference.RequestKey, intent), ct,
                DeploymentIdentity.GuidFor("action-deployment-quote/1", reference.OperationId!).ToString("N"));
            if (quoted.IsError || quoted.Result is null) return Fail<DeploymentReceipt>(quoted.Message);
            request = request with { NativeIntent = quoted.Result };
        }
        return await StartCoreAsync(reference.ActorUserId, reference.ActingTenantId, reference.RequestKey,
            request, authorize, ct, frozen, reference.IntentHash);
    }
}
