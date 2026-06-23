using System.Text.Json;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>
/// Handles <see cref="QuestNodeType.ComposeOutputs"/>. Gathers outputs from all
/// upstream nodes by reading the <see cref="QuestNodeExecution"/> rows the
/// manager pre-populates onto <see cref="QuestNodeExecutionContext.UpstreamExecutions"/>.
/// </summary>
/// <remarks>
/// Before the <c>quest-temporal-fork-model</c> track this handler read
/// <c>QuestNode.Output</c> directly off the quest definition's nodes. Runtime
/// output now lives on <see cref="QuestNodeExecution"/> keyed by
/// <c>(runId, nodeId)</c> — the manager prepares the upstream map so this
/// handler stays free of <c>IQuestNodeExecutionStore</c>.
/// </remarks>
public sealed class ComposeOutputsNodeHandler : IQuestNodeHandler
{
    public QuestNodeType NodeType => QuestNodeType.ComposeOutputs;

    public Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        // Identify the upstream nodes (by definition edges) and gather their
        // already-completed execution outputs by node id.
        var incomingNodeIds = context.Quest.Edges
            .Where(e => e.TargetNodeId == context.NodeId)
            .Select(e => e.SourceNodeId)
            .ToHashSet();

        var upstreamOutputs = new Dictionary<string, string>();
        foreach (var sourceNode in context.Quest.Nodes.Where(n => incomingNodeIds.Contains(n.Id)))
        {
            if (context.UpstreamExecutions.TryGetValue(sourceNode.Id, out var exec) && exec.Output != null)
            {
                upstreamOutputs[sourceNode.Name] = exec.Output;
            }
        }

        var outputJson = JsonSerializer.Serialize(upstreamOutputs, QuestNodeJson.Options);
        return Task.FromResult(QuestNodeResults.Ok(outputJson));
    }
}
