using System.Text.Json;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.ComposeOutputs"/>. Gathers outputs from all
/// upstream nodes by reading the <see cref="ActionNodeExecution"/> rows the
/// manager pre-populates onto <see cref="ActionNodeExecutionContext.UpstreamExecutions"/>.
/// </summary>
/// <remarks>
/// Before the <c>action-temporal-fork-model</c> track this handler read
/// <c>ActionNode.Output</c> directly off the action definition's nodes. Runtime
/// output now lives on <see cref="ActionNodeExecution"/> keyed by
/// <c>(runId, nodeId)</c> — the manager prepares the upstream map so this
/// handler stays free of <c>IActionNodeExecutionStore</c>.
/// </remarks>
public sealed class ComposeOutputsNodeHandler : IActionNodeHandler
{
    public ActionNodeType NodeType => ActionNodeType.ComposeOutputs;

    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        // Identify the upstream nodes (by definition edges) and gather their
        // already-completed execution outputs by node id.
        var incomingNodeIds = context.ActionDefinition.Edges
            .Where(e => e.TargetNodeId == context.NodeId)
            .Select(e => e.SourceNodeId)
            .ToHashSet();

        var upstreamOutputs = new Dictionary<string, string>();
        foreach (var sourceNode in context.ActionDefinition.Nodes.Where(n => incomingNodeIds.Contains(n.Id)))
        {
            if (context.UpstreamExecutions.TryGetValue(sourceNode.Id, out var exec) && exec.Output != null)
            {
                upstreamOutputs[sourceNode.Name] = exec.Output;
            }
        }

        var outputJson = JsonSerializer.Serialize(upstreamOutputs, ActionNodeJson.Options);
        return Task.FromResult(ActionNodeResults.Ok(outputJson));
    }
}
