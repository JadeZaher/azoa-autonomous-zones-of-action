using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Services.Deployments;

namespace AZOA.WebAPI.Services.Actions.Handlers;

public sealed class ActionGraphBundleDeployNodeHandler(IDeploymentService deployments) : IRecoverableActionNodeHandler
{
    /// <inheritdoc/>
    public ActionNodeType NodeType => ActionNodeType.ActionGraphBundleDeploy;

    /// <inheritdoc/>
    public ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash)
    {
        var key = DeploymentActionResolver.RequestKey(context.RunId, context.NodeId);
        return new(1, "deployment", key, DeploymentIdentity.Operation(context.ActingUserId, context.ActingTenantId, key),
            intentHash, context.ActingUserId, context.ActingTenantId);
    }

    /// <inheritdoc/>
    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<DeploymentNodeConfig>(context.Node.Config, nameof(NodeType), out var config, out var error)
            || config.Deployment is null) return ActionNodeHandlerResult.Invalid(error ?? "A typed deployment request is required.");
        using var document = JsonDocument.Parse(context.Node.Config);
        var frozen = new ActionFrozenNodeInput(1, context.RunId, context.NodeId, context.Node.NodeType,
            context.ActingUserId, context.ActingTenantId, document.RootElement.Clone(), context.UpstreamExecutions, context.AllRunExecutions);
        var canonical = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(frozen));
        var reference = CreateReference(context, CanonicalJson.HashCanonical(canonical));
        var result = await deployments.StartActionAsync(reference, canonical, true, ct);
        if (result.IsError || result.Result is null) return ActionNodeHandlerResult.Fail(result.Message);
        if (result.Result.OperationId != reference.OperationId)
            throw new InvalidOperationException("Deployment admission returned another operation identity.");
        return ActionNodeHandlerResult.Pending(DeploymentActionResolver.Observation(reference, result.Result));
    }
}
