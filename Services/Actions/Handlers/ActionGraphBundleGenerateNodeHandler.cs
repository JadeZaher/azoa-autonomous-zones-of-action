using System.Text.Json;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Services.Actions.Handlers;

public sealed class ActionGraphBundleGenerateNodeHandler(IDeploymentService deployments) : IActionNodeHandler
{
    /// <inheritdoc/>
    public ActionNodeType NodeType => ActionNodeType.ActionGraphBundleGenerate;

    /// <inheritdoc/>
    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<DeploymentGenerationNodeConfig>(context.Node.Config, nameof(NodeType), out var config, out var error))
            return ActionNodeHandlerResult.Invalid(error);
        if (config.BundleId == Guid.Empty || config.Request is null)
            return ActionNodeHandlerResult.Invalid("An owned bundle and typed generation request are required.");
        var result = await deployments.GenerateAsync(config.BundleId, context.ActingUserId, context.ActingTenantId, config.Request, ct);
        return result.IsError ? ActionNodeHandlerResult.Fail(result.Message)
            : ActionNodeHandlerResult.Ok(JsonSerializer.Serialize(result.Result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
