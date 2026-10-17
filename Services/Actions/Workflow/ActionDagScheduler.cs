using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions.Predicates;
using AZOA.WebAPI.Models.Deployments;
using System.Text.Json;

namespace AZOA.WebAPI.Services.Actions.Workflow;

/// <summary>Advances ready frozen DAG nodes through versioned outcomes and unique saga admission.</summary>
public sealed class ActionDagScheduler(IActionRunStore runs, IActionNodeExecutionStore executions,
    IActionWorkflowAdmissionStore admission)
{
    public async Task<ActionDagProgress> AdvanceAsync(ActionDefinition graph, Guid runId, CancellationToken ct = default)
    {
        var runResult = await runs.GetByIdAsync(runId, ct);
        if (runResult.IsError || runResult.Result is null) throw new InvalidOperationException(runResult.Message);
        var run = runResult.Result;
        if (run.Status.IsTerminal()) return new(run.Status, run.Status == ActionRunStatus.Failed);
        var unhandled = false;
        var manual = false;
        var runnable = false;
        IReadOnlyDictionary<Guid, ActionNodeExecution> byNode;
        for (var pass = 0; ; pass++)
        {
            runResult = await runs.GetByIdAsync(runId, ct);
            if (runResult.IsError || runResult.Result is null) throw new InvalidOperationException(runResult.Message);
            run = runResult.Result;
            if (run.Status.IsTerminal()) return new(run.Status, run.Status == ActionRunStatus.Failed, RunVersion: run.Version);
            var result = await executions.GetByRunIdAsync(runId, ct);
            if (result.IsError || result.Result is null) throw new InvalidOperationException(result.Message);
            byNode = result.Result.ToDictionary(row => row.NodeId);
            if (graph.Nodes.Any(node => !byNode.ContainsKey(node.Id)))
                throw new InvalidOperationException("Frozen DAG is missing an admitted node execution.");
            var changed = false;
            foreach (var node in graph.Nodes.OrderBy(node => node.ExecutionOrder).ThenBy(node => node.Id))
            {
                var row = byNode[node.Id].Clone();
                if (row.State != ActionNodeState.Pending) continue;
                if (OccurrenceState(run, node.Id, graph, byNode) == ActionOccurrenceState.Waiting) continue;
                var incoming = graph.Edges.Where(edge => edge.TargetNodeId == node.Id).ToArray();
                if (incoming.Any(edge => !Terminal(byNode[edge.SourceNodeId].State))) continue;
                var skip = OccurrenceState(run, node.Id, graph, byNode) == ActionOccurrenceState.Blocked
                    || incoming.Any(edge => edge.EdgeType != ActionEdgeType.OnFailure
                    && byNode[edge.SourceNodeId].State != ActionNodeState.Succeeded)
                    || (incoming.Any(edge => edge.EdgeType == ActionEdgeType.OnFailure)
                        && !incoming.Any(edge => edge.EdgeType == ActionEdgeType.OnFailure
                            && byNode[edge.SourceNodeId].State == ActionNodeState.Failed));
                string? predicateError = null;
                if (!skip)
                {
                    var scope = ActionConfigBindingResolver.BuildUpstreamScope(node, graph, byNode);
                    foreach (var (key, value) in ActionConfigBindingResolver.BuildRunScope(graph, byNode)) scope[key] = value;
                    foreach (var edge in incoming.Where(edge => edge.EdgeType == ActionEdgeType.Conditional
                                 && !string.IsNullOrWhiteSpace(edge.Condition)))
                    {
                        try { skip |= !GatePredicateEvaluator.Evaluate(edge.Condition!, scope); }
                        catch (GatePredicateException exception) { predicateError = exception.Message; break; }
                    }
                }
                if (!skip && predicateError is null) continue;
                row.State = predicateError is null ? ActionNodeState.Skipped : ActionNodeState.Failed;
                row.Error = predicateError;
                row.EndedAt = DateTime.UtcNow;
                await executions.UpdateAsync(row, ActionNodeState.Pending, ct);
                changed = true;
            }
            if (!changed) break;
            if (pass >= graph.Nodes.Count)
                throw new InvalidOperationException("DAG state kept changing while propagating terminal outcomes; retry from durable state.");
        }

        foreach (var node in graph.Nodes)
        {
            var row = byNode[node.Id];
            if (row.State is ActionNodeState.Failed or ActionNodeState.Cancelled)
                unhandled |= row.State == ActionNodeState.Cancelled
                    || Recovery(node.Id, graph, byNode, new HashSet<Guid>()) == FailureRecovery.Unrecoverable;
            if (ManualWait(node, row)) manual = true;
            if (row.State == ActionNodeState.Running) runnable = true;
        }
        if (!unhandled)
        {
            foreach (var node in graph.Nodes)
            {
                if (byNode[node.Id].State != ActionNodeState.Pending) continue;
                if (OccurrenceState(run, node.Id, graph, byNode) != ActionOccurrenceState.Ready) continue;
                var incoming = graph.Edges.Where(edge => edge.TargetNodeId == node.Id).ToArray();
                if (incoming.Any(edge => !Terminal(byNode[edge.SourceNodeId].State)
                    || ManualWait(graph.Nodes.Single(source => source.Id == edge.SourceNodeId), byNode[edge.SourceNodeId]))) continue;
                await admission.EnqueueNodeAsync(run.Id, node.Id,
                    SagaStep<ActionStepPayload>.Serialize(new ActionStepPayload(run.Id, run.ActionId, run.UserId, node.Id)), ct);
                runnable = true;
            }
        }
        var status = unhandled ? ActionRunStatus.AwaitingReconciliation
            : manual && !runnable ? ActionRunStatus.Suspended
            : graph.Nodes.All(node => Terminal(byNode[node.Id].State)) && !manual ? ActionRunStatus.Succeeded
            : ActionRunStatus.Running;
        if (run.Status != status)
        {
            var expected = run.Status;
            run.Status = status;
            if (status.IsTerminal()) run.EndedAt = DateTime.UtcNow;
            var updated = await runs.UpdateAsync(run, expected, ct);
            if (updated.IsError)
            {
                var current = await runs.GetByIdAsync(runId, ct);
                if (current.IsError || current.Result is null) throw new InvalidOperationException(current.Message);
                return new(current.Result.Status, false, DecisionChanged: true, RunVersion: current.Result.Version);
            }
        }
        return new(status, unhandled, graph.Nodes.Any(node => byNode[node.Id].State == ActionNodeState.Running), run.Version);
    }

    private static bool ManualWait(ActionNode node, ActionNodeExecution execution)
        => execution.State == ActionNodeState.Succeeded && !execution.ManualAdvanceReleased
            && WorkflowNodeConfig.Parse(node.Config).Item1 == WorkflowAdvance.Manual;

    public static ActionOccurrenceState OccurrenceState(ActionRun run, Guid entryNodeId, ActionDefinition graph,
        IReadOnlyDictionary<Guid, ActionNodeExecution> executions)
    {
        var boundaries = run.OccurrenceBoundariesJson is null ? []
            : JsonSerializer.Deserialize<List<DeploymentOccurrenceBoundary>>(run.OccurrenceBoundariesJson)
                ?? throw new InvalidOperationException("Frozen occurrence boundaries are unreadable.");
        var required = boundaries.Where(item => item.EntryNodeId == entryNodeId).SelectMany(item => item.RequiredNodeIds).Distinct().ToArray();
        if (required.Any(id => !executions.TryGetValue(id, out var row) || !Terminal(row.State)
            || ManualWait(graph.Nodes.Single(node => node.Id == id), row))) return ActionOccurrenceState.Waiting;
        return required.Any(id => executions[id].State == ActionNodeState.Cancelled
            || (executions[id].State == ActionNodeState.Failed
                && Recovery(id, graph, executions, new HashSet<Guid>()) != FailureRecovery.Recovered))
            ? ActionOccurrenceState.Blocked : ActionOccurrenceState.Ready;
    }

    private static bool Terminal(ActionNodeState state)
        => state is ActionNodeState.Succeeded or ActionNodeState.Failed or ActionNodeState.Skipped or ActionNodeState.Cancelled;

    private static FailureRecovery Recovery(Guid failed, ActionDefinition graph,
        IReadOnlyDictionary<Guid, ActionNodeExecution> rows, HashSet<Guid> visited)
    {
        if (!visited.Add(failed)) return FailureRecovery.Unrecoverable;
        var targets = graph.Edges.Where(edge => edge.SourceNodeId == failed && edge.EdgeType == ActionEdgeType.OnFailure)
            .Select(edge => edge.TargetNodeId).Distinct().ToArray();
        var pending = false;
        foreach (var target in targets)
        {
            var state = rows[target].State;
            if (state == ActionNodeState.Succeeded) return FailureRecovery.Recovered;
            if (state is ActionNodeState.Pending or ActionNodeState.Running) pending = true;
            if (state == ActionNodeState.Failed)
            {
                var nested = Recovery(target, graph, rows, new HashSet<Guid>(visited));
                if (nested == FailureRecovery.Recovered) return nested;
                pending |= nested == FailureRecovery.Pending;
            }
        }
        return pending ? FailureRecovery.Pending : FailureRecovery.Unrecoverable;
    }

    private enum FailureRecovery { Pending, Recovered, Unrecoverable }
}

/// <summary>Observed run progress and whether the saga must enter failure compensation.</summary>
public sealed record ActionDagProgress(ActionRunStatus Status, bool UnhandledFailure, bool HasAdmittedEffects = false,
    long RunVersion = 0, bool DecisionChanged = false);

public enum ActionOccurrenceState { Waiting, Ready, Blocked }
