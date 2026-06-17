using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;
using OASIS.WebAPI.Models.Sagas;
using OASIS.WebAPI.Sagas;
using OASIS.WebAPI.Services.Quest;
using OASIS.WebAPI.Services.Quest.Workflow;

namespace OASIS.WebAPI.Managers;

/// <summary>
/// Orchestrates quest execution against the per-run / per-(run,node) runtime
/// surface introduced by the <c>quest-temporal-fork-model</c> track. Holds a
/// <see cref="IQuestStore"/> (definitions, immutable in-flight),
/// <see cref="IQuestRunStore"/> (one row per execution attempt),
/// <see cref="IQuestNodeExecutionStore"/> (per-(run, node) state/output/error),
/// and <see cref="IQuestNodeHandlerRegistry"/> (the per-<see cref="QuestNodeType"/>
/// dispatch table).
/// </summary>
public class QuestManager : IQuestManager
{
    private readonly IQuestStore _questStore;
    private readonly IQuestRunStore _runStore;
    private readonly IQuestNodeExecutionStore _executionStore;
    private readonly IQuestDagValidator _dagValidator;
    private readonly IQuestNodeHandlerRegistry _registry;
    private readonly ISagaStore _sagaStore;

    public QuestManager(
        IQuestStore questStore,
        IQuestRunStore runStore,
        IQuestNodeExecutionStore executionStore,
        IQuestDagValidator dagValidator,
        IQuestNodeHandlerRegistry registry,
        ISagaStore sagaStore)
    {
        _questStore = questStore;
        _runStore = runStore;
        _executionStore = executionStore;
        _dagValidator = dagValidator;
        _registry = registry;
        _sagaStore = sagaStore;
    }

    /// <summary>
    /// Loads a quest and rejects when it is owned by a different avatar. Returns
    /// the loaded quest on success, or an error result to surface verbatim.
    /// </summary>
    private async Task<OASISResult<Quest>> LoadOwnedQuestAsync(Guid questId, Guid avatarId)
    {
        var questResult = await _questStore.GetQuestAsync(questId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<Quest> { IsError = true, Message = questResult.Message ?? "Quest not found." };
        if (questResult.Result.AvatarId != avatarId)
            return new OASISResult<Quest> { IsError = true, Message = "Quest is owned by a different avatar." };
        return questResult;
    }

    /// <summary>
    /// Loads a run and rejects when it is owned by a different avatar. Returns
    /// the loaded run on success, or an error result to surface verbatim.
    /// </summary>
    private async Task<OASISResult<QuestRun>> LoadOwnedRunAsync(Guid runId, Guid avatarId)
    {
        var runResult = await _runStore.GetByIdAsync(runId);
        if (runResult.IsError || runResult.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = runResult.Message ?? "Quest run not found." };
        if (runResult.Result.AvatarId != avatarId)
            return new OASISResult<QuestRun> { IsError = true, Message = "Quest run is owned by a different avatar." };
        return runResult;
    }

    // ═══════════════════════════════════════════════════════════════════
    // QUEST CRUD
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<Quest>> CreateAsync(QuestCreateModel model, Guid avatarId, OASISRequest? request = null)
    {
        var quest = new Quest
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            Description = model.Description,
            AvatarId = avatarId,
            CreatedDate = DateTime.UtcNow
        };

        // Create nodes with new Ids
        var nodeIds = new List<Guid>();
        foreach (var nodeModel in model.Nodes)
        {
            var node = new QuestNode
            {
                Id = Guid.NewGuid(),
                QuestId = quest.Id,
                Name = nodeModel.Name,
                NodeType = nodeModel.NodeType,
                Config = nodeModel.Config,
                IsEntry = nodeModel.IsEntry,
                IsTerminal = nodeModel.IsTerminal,
                NodeTemplateId = nodeModel.NodeTemplateId
            };
            quest.Nodes.Add(node);
            nodeIds.Add(node.Id);
        }

        // Map edge indices to node Ids
        foreach (var edgeModel in model.Edges)
        {
            if (edgeModel.SourceNodeId < 0 || edgeModel.SourceNodeId >= nodeIds.Count ||
                edgeModel.TargetNodeId < 0 || edgeModel.TargetNodeId >= nodeIds.Count)
            {
                return new OASISResult<Quest> { IsError = true, Message = $"Edge index out of range. Source={edgeModel.SourceNodeId}, Target={edgeModel.TargetNodeId}, NodeCount={nodeIds.Count}." };
            }

            var edge = new QuestEdge
            {
                Id = Guid.NewGuid(),
                QuestId = quest.Id,
                SourceNodeId = nodeIds[edgeModel.SourceNodeId],
                TargetNodeId = nodeIds[edgeModel.TargetNodeId],
                Condition = edgeModel.Condition,
                EdgeType = edgeModel.EdgeType
            };
            quest.Edges.Add(edge);
        }

        return await _questStore.UpsertQuestAsync(quest);
    }

    public async Task<OASISResult<Quest>> GetAsync(Guid id, Guid avatarId, OASISRequest? request = null)
    {
        return await LoadOwnedQuestAsync(id, avatarId);
    }

    public async Task<OASISResult<IEnumerable<Quest>>> GetByAvatarAsync(Guid avatarId, OASISRequest? request = null)
    {
        return await _questStore.GetQuestsByAvatarAsync(avatarId);
    }

    public async Task<OASISResult<Quest>> UpdateAsync(Guid id, QuestUpdateModel model, Guid avatarId, OASISRequest? request = null)
    {
        var existing = await LoadOwnedQuestAsync(id, avatarId);
        if (existing.IsError || existing.Result == null) return existing;

        var quest = existing.Result;
        if (model.Name != null) quest.Name = model.Name;
        if (model.Description != null) quest.Description = model.Description;
        // model.Status is intentionally ignored — runtime status moved to
        // QuestRun.Status (see ADR §2.2). The field on QuestUpdateModel is
        // retained for API back-compat but has no effect on the definition.

        return await _questStore.UpsertQuestAsync(quest);
    }

    public async Task<OASISResult<bool>> DeleteAsync(Guid id, Guid avatarId, OASISRequest? request = null)
    {
        var owned = await LoadOwnedQuestAsync(id, avatarId);
        if (owned.IsError || owned.Result == null)
            return new OASISResult<bool> { IsError = true, Message = owned.Message };

        return await _questStore.DeleteQuestAsync(id);
    }

    // ═══════════════════════════════════════════════════════════════════
    // DAG VALIDATION
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<bool>> ValidateDAGAsync(Guid questId, OASISRequest? request = null)
    {
        var questResult = await _questStore.GetQuestAsync(questId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;

        // QuestDagValidator is the single ExecutionOrder authority — Validate
        // mutates node.ExecutionOrder in-place on the quest graph.
        var validation = _dagValidator.Validate(quest);

        if (!validation.IsValid)
        {
            return new OASISResult<bool>
            {
                IsError = true,
                Result = false,
                Message = $"DAG validation failed: {string.Join("; ", validation.Errors)}"
            };
        }

        await _questStore.UpsertQuestAsync(quest);

        return new OASISResult<bool> { Result = true, Message = "DAG is valid." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // EXECUTION
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<QuestRun>> ExecuteAsync(Guid questId, Guid avatarId, OASISRequest? request = null)
    {
        var owned = await LoadOwnedQuestAsync(questId, avatarId);
        if (owned.IsError || owned.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = owned.Message };

        // Validate DAG first (also assigns ExecutionOrder onto definition nodes).
        var validationResult = await ValidateDAGAsync(questId, request);
        if (validationResult.IsError)
            return new OASISResult<QuestRun> { IsError = true, Message = validationResult.Message };

        var questResult = await _questStore.GetQuestAsync(questId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;

        // Create the QuestRun upfront — Pending → Running on first node claim.
        var run = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Pending,
            StartedAt = DateTime.UtcNow
        };
        var createRun = await _runStore.CreateAsync(run);
        if (createRun.IsError || createRun.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = createRun.Message };

        // Pre-create one QuestNodeExecution(Pending) per quest node.
        foreach (var node in quest.Nodes)
        {
            var exec = new QuestNodeExecution
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                NodeId = node.Id,
                State = QuestNodeState.Pending,
                StartedAt = DateTime.UtcNow
            };
            var createExec = await _executionStore.CreateAsync(exec);
            if (createExec.IsError)
                return new OASISResult<QuestRun> { IsError = true, Message = createExec.Message };
        }

        // Status: Pending → Running (first claim).
        run.Status = QuestRunStatus.Running;
        await _runStore.UpdateAsync(run);

        // Track per-node executions in-memory for ComposeOutputs upstream lookup.
        var executionsByNode = new Dictionary<Guid, QuestNodeExecution>();

        // Execute nodes in topological order.
        var sortedNodes = quest.Nodes.OrderBy(n => n.ExecutionOrder).ToList();

        foreach (var node in sortedNodes)
        {
            // Conditional / failed-predecessor skipping — uses upstream executions
            // (not the obsolete in-place QuestNode.State).
            var incomingEdges = quest.Edges.Where(e => e.TargetNodeId == node.Id).ToList();
            var shouldSkip = false;

            foreach (var edge in incomingEdges)
            {
                executionsByNode.TryGetValue(edge.SourceNodeId, out var sourceExec);
                var sourceState = sourceExec?.State;

                if (edge.EdgeType == QuestEdgeType.Conditional && !string.IsNullOrEmpty(edge.Condition))
                {
                    if (sourceState == QuestNodeState.Failed || sourceState == QuestNodeState.Skipped)
                    {
                        shouldSkip = true;
                        break;
                    }
                }

                if (edge.EdgeType == QuestEdgeType.Control && sourceState == QuestNodeState.Failed)
                {
                    shouldSkip = true;
                    break;
                }
            }

            var execution = (await _executionStore.GetByRunAndNodeAsync(run.Id, node.Id)).Result!;

            if (shouldSkip)
            {
                execution.State = QuestNodeState.Skipped;
                execution.EndedAt = DateTime.UtcNow;
                // HIGH#7 G2 guard: only mark Skipped if the row is still
                // Pending. If a concurrent ForkAsync flipped it to Cancelled
                // we must not silently overwrite — the store returns an
                // error result and we simply re-read the latest state for
                // downstream ComposeOutputs lookup.
                var skipUpdate = await _executionStore.UpdateAsync(
                    execution, expectedState: QuestNodeState.Pending);
                if (skipUpdate.IsError)
                {
                    execution = (await _executionStore.GetByRunAndNodeAsync(run.Id, node.Id)).Result ?? execution;
                }
                executionsByNode[node.Id] = execution;
                continue;
            }

            // Claim the row (Pending → Running). G2 conditional-update guard.
            var claim = await _executionStore.TryClaimPendingAsync(run.Id, node.Id);
            if (claim.IsError || claim.Result == null)
            {
                // Either missing or already-claimed (lost race). Treat as fail.
                execution.State = QuestNodeState.Failed;
                execution.Error = claim.Message ?? "Failed to claim node for execution.";
                execution.EndedAt = DateTime.UtcNow;
                // Best-effort unconditional update — the claim already
                // observed a drift, so a second guard would be redundant.
                await _executionStore.UpdateAsync(execution);
                executionsByNode[node.Id] = execution;
                break;
            }
            execution = claim.Result!;

            // Build upstream-output map for ComposeOutputs (predecessors only).
            var upstream = new Dictionary<Guid, QuestNodeExecution>();
            foreach (var edge in incomingEdges)
            {
                if (executionsByNode.TryGetValue(edge.SourceNodeId, out var pe))
                    upstream[edge.SourceNodeId] = pe;
            }

            QuestNodeHandlerResult result;
            if (!_registry.TryGet(node.NodeType, out var handler))
            {
                result = QuestNodeResults.Fail($"Unsupported node type: {node.NodeType}");
            }
            else
            {
                try
                {
                    var ctx = new QuestNodeExecutionContext(run.Id, node.Id, quest, upstream);
                    result = await handler.HandleAsync(ctx);
                }
                catch (Exception ex)
                {
                    result = QuestNodeResults.Fail(ex.Message);
                }
            }

            if (result.IsError)
            {
                execution.State = QuestNodeState.Failed;
                execution.Error = result.Message;
            }
            else
            {
                execution.State = QuestNodeState.Succeeded;
                execution.Output = result.Output;
            }
            execution.EndedAt = DateTime.UtcNow;
            // HIGH#7 — only commit the terminal state when the row is still
            // Running. A concurrent fork (or supervisor-fail) may have
            // flipped the row to Cancelled between our claim and our
            // completion; in that case the guard rejects the overwrite and
            // we accept the fork's decision rather than resurrecting the
            // execution into Succeeded/Failed.
            var terminalUpdate = await _executionStore.UpdateAsync(
                execution, expectedState: QuestNodeState.Running);
            if (terminalUpdate.IsError)
            {
                execution = (await _executionStore.GetByRunAndNodeAsync(run.Id, node.Id)).Result ?? execution;
            }
            executionsByNode[node.Id] = execution;
        }

        // Determine overall run status from the per-node executions.
        var allExecs = (await _executionStore.GetByRunIdAsync(run.Id)).Result ?? Enumerable.Empty<QuestNodeExecution>();
        run.Status = allExecs.Any(e => e.State == QuestNodeState.Failed)
            ? QuestRunStatus.Failed
            : QuestRunStatus.Succeeded;
        run.EndedAt = DateTime.UtcNow;
        var updated = await _runStore.UpdateAsync(run);

        return new OASISResult<QuestRun> { Result = updated.Result, Message = $"Quest run {run.Status}." };
    }

    public async Task<OASISResult<QuestNodeExecution>> ExecuteNodeAsync(Guid questId, Guid nodeId, Guid avatarId, OASISRequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<QuestNodeExecution> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        var node = quest.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return new OASISResult<QuestNodeExecution> { IsError = true, Message = "Node not found." };

        // Single-node execution creates an ad-hoc QuestRun that lives just long
        // enough to record this one node's outcome. This preserves the
        // historic ExecuteNodeAsync entry point (used by the QuestController)
        // while keeping the new (runId, nodeId) invariant: no state ever lands
        // on the QuestNode itself.
        var run = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await _runStore.CreateAsync(run);

        var execution = new QuestNodeExecution
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            NodeId = node.Id,
            State = QuestNodeState.Running,
            StartedAt = DateTime.UtcNow
        };
        await _executionStore.CreateAsync(execution);

        QuestNodeHandlerResult result;
        if (!_registry.TryGet(node.NodeType, out var handler))
        {
            result = QuestNodeResults.Fail($"Unsupported node type: {node.NodeType}");
        }
        else
        {
            try
            {
                var ctx = new QuestNodeExecutionContext(run.Id, node.Id, quest);
                result = await handler.HandleAsync(ctx);
            }
            catch (Exception ex)
            {
                result = QuestNodeResults.Fail(ex.Message);
            }
        }

        if (result.IsError)
        {
            execution.State = QuestNodeState.Failed;
            execution.Error = result.Message;
        }
        else
        {
            execution.State = QuestNodeState.Succeeded;
            execution.Output = result.Output;
        }
        execution.EndedAt = DateTime.UtcNow;
        // HIGH#7 — same G2 guard as the in-loop terminal update at line ~308.
        // The ad-hoc single-node run created the execution in Running state,
        // so we only commit Succeeded/Failed when the row is still Running.
        await _executionStore.UpdateAsync(execution, expectedState: QuestNodeState.Running);

        run.Status = result.IsError ? QuestRunStatus.Failed : QuestRunStatus.Succeeded;
        run.EndedAt = DateTime.UtcNow;
        await _runStore.UpdateAsync(run);

        return result.IsError
            ? new OASISResult<QuestNodeExecution> { IsError = true, Result = execution, Message = result.Message ?? string.Empty }
            : new OASISResult<QuestNodeExecution> { Result = execution, Message = "Node executed successfully." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // FORK
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<QuestRun>> ForkAsync(Guid runId, Guid atNodeId, string reason, Guid avatarId, OASISRequest? request = null)
    {
        var parentResult = await LoadOwnedRunAsync(runId, avatarId);
        if (parentResult.IsError || parentResult.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = parentResult.Message };

        var parent = parentResult.Result;

        // State-machine guard: only Running runs are forkable. Succeeded runs
        // are re-runnable (new root) but not forkable; Failed/Forked/Cancelled
        // are terminal. See ADR §2.3.
        if (parent.Status != QuestRunStatus.Running)
        {
            return new OASISResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot fork run {runId}: status is {parent.Status} (only Running runs are forkable)."
            };
        }

        var questResult = await _questStore.GetQuestAsync(parent.QuestId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        var forkPointNode = quest.Nodes.FirstOrDefault(n => n.Id == atNodeId);
        if (forkPointNode == null)
        {
            return new OASISResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot fork at node {atNodeId}: not present in quest {parent.QuestId} definition."
            };
        }
        var forkPoint = forkPointNode.ExecutionOrder;

        // Create the child run with lineage fields populated.
        var child = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = parent.QuestId,
            AvatarId = parent.AvatarId,
            Status = QuestRunStatus.Pending,
            StartedAt = DateTime.UtcNow,
            ParentRunId = parent.Id,
            ForkedAtNodeId = atNodeId,
            ForkReason = reason
        };
        var createChild = await _runStore.CreateAsync(child);
        if (createChild.IsError || createChild.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = createChild.Message };

        // Copy-by-reference: for nodes with ExecutionOrder < forkPoint, the
        // parent's execution row is shared with the child. The InMemory
        // implementation does this by creating a new (childRunId, nodeId)
        // row that mirrors the parent's execution state (same Id, same
        // Output/Error/State). The SurrealDB write-through (surrealdb-migration
        // tasks 9–10) materializes this as a `RELATE quest_run -> executes ->
        // quest_node_execution` edge — no duplication. See SURREAL-SCHEMA-HINTS §6.2.
        var parentExecs = (await _executionStore.GetByRunIdAsync(parent.Id)).Result
            ?? Enumerable.Empty<QuestNodeExecution>();
        foreach (var parentExec in parentExecs)
        {
            var parentNode = quest.Nodes.FirstOrDefault(n => n.Id == parentExec.NodeId);
            if (parentNode == null) continue;
            if (parentNode.ExecutionOrder >= forkPoint) continue;

            // Mirror the parent's completed execution onto the child run.
            var mirror = new QuestNodeExecution
            {
                Id = Guid.NewGuid(),
                RunId = child.Id,
                NodeId = parentExec.NodeId,
                State = parentExec.State,
                Output = parentExec.Output,
                Error = parentExec.Error,
                StartedAt = parentExec.StartedAt,
                EndedAt = parentExec.EndedAt
            };
            await _executionStore.CreateAsync(mirror);
        }

        // Cancel parent's in-flight (Pending / Running) node executions.
        // HIGH#7 — the state-machine guard prevents a late-arriving terminal
        // transition (e.g. a node that succeeded between our GetByRunId snap
        // and the cancel write) from being silently overwritten by Cancelled.
        // We pass the freshly-observed state as the guard; if the store
        // returns an error, the row already moved past the in-flight window
        // and we accept its terminal state (no resurrection).
        foreach (var pe in parentExecs)
        {
            if (pe.State != QuestNodeState.Pending && pe.State != QuestNodeState.Running) continue;
            var observedState = pe.State;
            pe.State = QuestNodeState.Cancelled;
            pe.EndedAt = DateTime.UtcNow;
            await _executionStore.UpdateAsync(pe, expectedState: observedState);
        }

        // Transition parent Running → Forked (terminal).
        parent.Status = QuestRunStatus.Forked;
        parent.EndedAt = DateTime.UtcNow;
        await _runStore.UpdateAsync(parent);

        return new OASISResult<QuestRun> { Result = child, Message = "Fork created." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // SUPERVISOR-DRIVEN FAIL
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<QuestRun>> MarkRunFailedAsync(Guid runId, string reason, Guid avatarId, OASISRequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, avatarId);
        if (runResult.IsError || runResult.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = runResult.Message };

        var run = runResult.Result;
        if (run.Status != QuestRunStatus.Running)
        {
            return new OASISResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot mark run {runId} failed: status is {run.Status} (only Running runs accept supervisor fail)."
            };
        }

        // Cancel any in-flight node executions (same shape as fork).
        // HIGH#7 — same state-machine guard as ForkAsync (line ~509).
        var execs = (await _executionStore.GetByRunIdAsync(run.Id)).Result
            ?? Enumerable.Empty<QuestNodeExecution>();
        foreach (var exec in execs)
        {
            if (exec.State != QuestNodeState.Pending && exec.State != QuestNodeState.Running) continue;
            var observedState = exec.State;
            exec.State = QuestNodeState.Cancelled;
            exec.EndedAt = DateTime.UtcNow;
            await _executionStore.UpdateAsync(exec, expectedState: observedState);
        }

        run.Status = QuestRunStatus.Failed;
        run.FailReason = reason;
        run.EndedAt = DateTime.UtcNow;
        var updated = await _runStore.UpdateAsync(run);

        return new OASISResult<QuestRun> { Result = updated.Result, Message = $"Run marked failed: {reason}" };
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<QuestTemplate>> CreateTemplateAsync(QuestTemplateCreateModel model, Guid avatarId, OASISRequest? request = null)
    {
        var template = new QuestTemplate
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            Description = model.Description,
            AuthorAvatarId = avatarId,
            Parameters = model.Parameters,
            Version = model.Version,
            IsPublic = model.IsPublic,
            Tags = model.Tags
        };

        // Build template nodes from the create model
        var slotIds = new List<string>();
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            var nodeModel = model.Nodes[i];
            var slotId = $"slot_{i}";
            slotIds.Add(slotId);

            template.Nodes.Add(new QuestTemplateNode
            {
                Id = Guid.NewGuid(),
                TemplateId = template.Id,
                SlotId = slotId,
                NodeTemplateId = nodeModel.NodeTemplateId ?? Guid.Empty,
                ParamOverrides = nodeModel.Config,
                IsEntry = nodeModel.IsEntry,
                IsTerminal = nodeModel.IsTerminal
            });
        }

        foreach (var edgeModel in model.Edges)
        {
            if (edgeModel.SourceNodeId < 0 || edgeModel.SourceNodeId >= slotIds.Count ||
                edgeModel.TargetNodeId < 0 || edgeModel.TargetNodeId >= slotIds.Count)
            {
                return new OASISResult<QuestTemplate> { IsError = true, Message = "Edge index out of range." };
            }

            template.Edges.Add(new QuestTemplateEdge
            {
                Id = Guid.NewGuid(),
                TemplateId = template.Id,
                SourceSlotId = slotIds[edgeModel.SourceNodeId],
                TargetSlotId = slotIds[edgeModel.TargetNodeId],
                EdgeType = edgeModel.EdgeType
            });
        }

        return await _questStore.UpsertQuestTemplateAsync(template);
    }

    public async Task<OASISResult<QuestTemplate>> GetTemplateAsync(Guid id, OASISRequest? request = null)
    {
        return await _questStore.GetQuestTemplateAsync(id);
    }

    public async Task<OASISResult<IEnumerable<QuestTemplate>>> ListTemplatesAsync(OASISRequest? request = null)
    {
        return await _questStore.GetAllQuestTemplatesAsync();
    }

    public async Task<OASISResult<Quest>> InstantiateTemplateAsync(Guid templateId, Guid avatarId, Dictionary<string, string>? parameters = null, OASISRequest? request = null)
    {
        var templateResult = await _questStore.GetQuestTemplateAsync(templateId);
        if (templateResult.IsError || templateResult.Result == null)
            return new OASISResult<Quest> { IsError = true, Message = templateResult.Message };

        var template = templateResult.Result;

        // Load node templates referenced by this quest template
        var nodeTemplatesResult = await _questStore.GetAllQuestNodeTemplatesAsync();
        var nodeTemplates = (nodeTemplatesResult.Result ?? Enumerable.Empty<QuestNodeTemplate>())
            .ToDictionary(nt => nt.Id);

        var quest = new Quest
        {
            Id = Guid.NewGuid(),
            Name = template.Name,
            Description = template.Description,
            AvatarId = avatarId,
            TemplateId = templateId,
            CreatedDate = DateTime.UtcNow
        };

        // Map slotId -> new node Id
        var slotToNodeId = new Dictionary<string, Guid>();

        foreach (var templateNode in template.Nodes)
        {
            var nodeId = Guid.NewGuid();
            slotToNodeId[templateNode.SlotId] = nodeId;

            // Resolve config from node template with param overrides
            var config = templateNode.ParamOverrides;
            if (nodeTemplates.TryGetValue(templateNode.NodeTemplateId, out var nodeTemplate))
            {
                config = string.IsNullOrEmpty(templateNode.ParamOverrides) || templateNode.ParamOverrides == "{}"
                    ? nodeTemplate.DefaultConfig
                    : templateNode.ParamOverrides;
            }

            // Apply parameter substitutions
            if (parameters != null)
            {
                foreach (var (key, value) in parameters)
                    config = config.Replace($"{{{{{key}}}}}", value);
            }

            var nodeType = nodeTemplate?.NodeType ?? QuestNodeType.HolonGet;

            quest.Nodes.Add(new QuestNode
            {
                Id = nodeId,
                QuestId = quest.Id,
                NodeTemplateId = templateNode.NodeTemplateId,
                NodeType = nodeType,
                Name = nodeTemplate?.Name ?? templateNode.SlotId,
                Config = config,
                IsEntry = templateNode.IsEntry,
                IsTerminal = templateNode.IsTerminal
            });
        }

        foreach (var templateEdge in template.Edges)
        {
            if (!slotToNodeId.TryGetValue(templateEdge.SourceSlotId, out var sourceId) ||
                !slotToNodeId.TryGetValue(templateEdge.TargetSlotId, out var targetId))
                continue;

            quest.Edges.Add(new QuestEdge
            {
                Id = Guid.NewGuid(),
                QuestId = quest.Id,
                SourceNodeId = sourceId,
                TargetNodeId = targetId,
                EdgeType = templateEdge.EdgeType
            });
        }

        return await _questStore.UpsertQuestAsync(quest);
    }

    // ═══════════════════════════════════════════════════════════════════
    // NODE TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<QuestNodeTemplate>> CreateNodeTemplateAsync(QuestNodeTemplateCreateModel model, Guid avatarId, OASISRequest? request = null)
    {
        var template = new QuestNodeTemplate
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            NodeType = model.NodeType,
            Description = model.Description,
            DefaultConfig = model.DefaultConfig,
            ConfigSchema = model.ConfigSchema,
            InputSchema = model.InputSchema,
            OutputSchema = model.OutputSchema,
            Version = model.Version,
            AuthorAvatarId = avatarId,
            IsPublic = model.IsPublic,
            Tags = model.Tags
        };

        return await _questStore.UpsertQuestNodeTemplateAsync(template);
    }

    public async Task<OASISResult<IEnumerable<QuestNodeTemplate>>> ListNodeTemplatesAsync(OASISRequest? request = null)
    {
        return await _questStore.GetAllQuestNodeTemplatesAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    // QUEST NODES SUB-RESOURCE (post-hoc edits on a persisted Quest)
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<IEnumerable<QuestNode>>> ListNodesAsync(Guid questId, Guid avatarId, OASISRequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<IEnumerable<QuestNode>> { IsError = true, Message = questResult.Message };

        return new OASISResult<IEnumerable<QuestNode>>
        {
            Result = questResult.Result.Nodes,
            Message = "Success"
        };
    }

    public async Task<OASISResult<QuestNode>> AddNodeAsync(Guid questId, QuestNodeCreateModel model, Guid avatarId, OASISRequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<QuestNode> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;

        var node = new QuestNode
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            Name = model.Name,
            NodeType = model.NodeType,
            Config = model.Config,
            IsEntry = model.IsEntry,
            IsTerminal = model.IsTerminal,
            NodeTemplateId = model.NodeTemplateId
        };
        quest.Nodes.Add(node);

        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new OASISResult<QuestNode> { IsError = true, Message = upsert.Message };

        return new OASISResult<QuestNode> { Result = node, Message = "Node added." };
    }

    public async Task<OASISResult<QuestNode>> UpdateNodeAsync(Guid questId, Guid nodeId, QuestNodeUpdateModel model, Guid avatarId, OASISRequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<QuestNode> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        var node = quest.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return new OASISResult<QuestNode> { IsError = true, Message = $"Node {nodeId} not found in quest {questId}." };

        // Patch semantics: only non-null fields are applied.
        if (model.Name != null) node.Name = model.Name;
        if (model.Config != null) node.Config = model.Config;
        if (model.IsEntry.HasValue) node.IsEntry = model.IsEntry.Value;
        if (model.IsTerminal.HasValue) node.IsTerminal = model.IsTerminal.Value;

        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new OASISResult<QuestNode> { IsError = true, Message = upsert.Message };

        return new OASISResult<QuestNode> { Result = node, Message = "Node updated." };
    }

    public async Task<OASISResult<bool>> DeleteNodeAsync(Guid questId, Guid nodeId, Guid avatarId, OASISRequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        var node = quest.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return new OASISResult<bool> { IsError = true, Message = $"Node {nodeId} not found in quest {questId}." };

        // Reject if node has any edges referencing it — orphaning edges would
        // produce an invalid DAG. Callers must clear edges first via
        // RemoveEdgeAsync. This preserves the graph invariant that every edge
        // endpoint references an existing node.
        var referencingEdges = quest.Edges
            .Where(e => e.SourceNodeId == nodeId || e.TargetNodeId == nodeId)
            .ToList();
        if (referencingEdges.Count > 0)
        {
            return new OASISResult<bool>
            {
                IsError = true,
                Result = false,
                Message = $"Cannot delete node {nodeId}: {referencingEdges.Count} edge(s) reference it. Remove the referencing edges first."
            };
        }

        quest.Nodes.Remove(node);
        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new OASISResult<bool> { IsError = true, Message = upsert.Message };

        return new OASISResult<bool> { Result = true, Message = "Node deleted." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // QUEST EDGES SUB-RESOURCE
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<QuestEdge>> AddEdgeAsync(Guid questId, QuestEdgeAddModel model, Guid avatarId, OASISRequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<QuestEdge> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;

        // Endpoint existence guard — both ends must reference nodes already in
        // the quest. Without this an invalid graph can be persisted and only
        // discovered at validate/execute time.
        if (quest.Nodes.All(n => n.Id != model.SourceNodeId))
            return new OASISResult<QuestEdge> { IsError = true, Message = $"SourceNodeId {model.SourceNodeId} is not present in quest {questId}." };
        if (quest.Nodes.All(n => n.Id != model.TargetNodeId))
            return new OASISResult<QuestEdge> { IsError = true, Message = $"TargetNodeId {model.TargetNodeId} is not present in quest {questId}." };
        if (model.SourceNodeId == model.TargetNodeId)
            return new OASISResult<QuestEdge> { IsError = true, Message = "SourceNodeId and TargetNodeId must not be the same node (self-loops not allowed)." };

        var edge = new QuestEdge
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            SourceNodeId = model.SourceNodeId,
            TargetNodeId = model.TargetNodeId,
            Condition = model.Condition,
            EdgeType = model.EdgeType
        };
        quest.Edges.Add(edge);

        // Cycle guard: run the DAG validator after staging the new edge and
        // reject only on cycle-class errors. We deliberately ignore the
        // entry/terminal/orphan checks because a Quest is editable in-flight
        // — terminal/entry flags and full reachability are only required
        // immediately before ExecuteAsync (which runs the full ValidateDAGAsync).
        // Allowing orphan-class errors here would prevent any incremental
        // edge wiring on a partially-built graph.
        var validation = _dagValidator.Validate(quest);
        var cycleErrors = validation.Errors
            .Where(e => e.Contains("Cycle detected", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (cycleErrors.Count > 0)
        {
            quest.Edges.Remove(edge);
            return new OASISResult<QuestEdge>
            {
                IsError = true,
                Message = $"Edge would invalidate DAG: {string.Join("; ", cycleErrors)}"
            };
        }

        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new OASISResult<QuestEdge> { IsError = true, Message = upsert.Message };

        return new OASISResult<QuestEdge> { Result = edge, Message = "Edge added." };
    }

    public async Task<OASISResult<bool>> RemoveEdgeAsync(Guid questId, Guid edgeId, Guid avatarId, OASISRequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        var edge = quest.Edges.FirstOrDefault(e => e.Id == edgeId);
        if (edge == null)
            return new OASISResult<bool> { IsError = true, Message = $"Edge {edgeId} not found in quest {questId}." };

        quest.Edges.Remove(edge);
        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new OASISResult<bool> { IsError = true, Message = upsert.Message };

        return new OASISResult<bool> { Result = true, Message = "Edge removed." };
    }

    public async Task<OASISResult<IEnumerable<Guid>>> GetTopologicalOrderAsync(Guid questId, Guid avatarId, OASISRequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<IEnumerable<Guid>> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;

        // QuestDagValidator is the single ExecutionOrder authority — Validate
        // mutates node.ExecutionOrder in-place. Persist the order so subsequent
        // reads of the quest definition see the validator-assigned positions.
        var validation = _dagValidator.Validate(quest);
        if (!validation.IsValid)
        {
            return new OASISResult<IEnumerable<Guid>>
            {
                IsError = true,
                Message = $"DAG validation failed: {string.Join("; ", validation.Errors)}"
            };
        }

        await _questStore.UpsertQuestAsync(quest);

        var ordered = quest.Nodes
            .OrderBy(n => n.ExecutionOrder)
            .Select(n => n.Id)
            .ToList();

        return new OASISResult<IEnumerable<Guid>> { Result = ordered, Message = "Success" };
    }

    // ═══════════════════════════════════════════════════════════════════
    // QUEST DEPENDENCIES SUB-RESOURCE
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<QuestDependency>> AddDependencyAsync(Guid questId, QuestDependencyCreateModel model, Guid avatarId, OASISRequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<QuestDependency> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;

        if (model.DependsOnQuestId == Guid.Empty)
            return new OASISResult<QuestDependency> { IsError = true, Message = "DependsOnQuestId must not be an empty GUID." };
        if (model.DependsOnQuestId == questId)
            return new OASISResult<QuestDependency> { IsError = true, Message = "A quest may not depend on itself." };

        var dependency = new QuestDependency
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            DependsOnQuestId = model.DependsOnQuestId,
            DependsOnNodeId = model.DependsOnNodeId,
            DependencyType = model.DependencyType
        };
        quest.Dependencies.Add(dependency);

        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new OASISResult<QuestDependency> { IsError = true, Message = upsert.Message };

        return new OASISResult<QuestDependency> { Result = dependency, Message = "Dependency added." };
    }

    public async Task<OASISResult<bool>> RemoveDependencyAsync(Guid questId, Guid depId, Guid avatarId, OASISRequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        var dep = quest.Dependencies.FirstOrDefault(d => d.Id == depId);
        if (dep == null)
            return new OASISResult<bool> { IsError = true, Message = $"Dependency {depId} not found in quest {questId}." };

        quest.Dependencies.Remove(dep);
        var upsert = await _questStore.UpsertQuestAsync(quest);
        if (upsert.IsError)
            return new OASISResult<bool> { IsError = true, Message = upsert.Message };

        return new OASISResult<bool> { Result = true, Message = "Dependency removed." };
    }

    public async Task<OASISResult<DependencyCheckResult>> CheckDependenciesAsync(Guid questId, Guid avatarId, OASISRequest? request = null)
    {
        var questResult = await LoadOwnedQuestAsync(questId, avatarId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<DependencyCheckResult> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        var unsatisfied = new List<Guid>();

        foreach (var dep in quest.Dependencies)
        {
            var runs = await _runStore.GetByQuestIdAsync(dep.DependsOnQuestId);
            var anySucceeded = runs.Result?.Any(r => r.Status == QuestRunStatus.Succeeded) ?? false;
            if (!anySucceeded)
                unsatisfied.Add(dep.Id);
        }

        var check = new DependencyCheckResult
        {
            AllSatisfied = unsatisfied.Count == 0,
            UnsatisfiedDependencyIds = unsatisfied,
            Message = unsatisfied.Count == 0
                ? "All dependencies satisfied."
                : $"{unsatisfied.Count} dependency(ies) not yet satisfied."
        };

        return new OASISResult<DependencyCheckResult> { Result = check, Message = "Success" };
    }

    // ═══════════════════════════════════════════════════════════════════
    // QUESTRUN READ SURFACE
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<QuestRun>> GetRunAsync(Guid runId, Guid avatarId, OASISRequest? request = null)
    {
        return await LoadOwnedRunAsync(runId, avatarId);
    }

    public async Task<OASISResult<IEnumerable<QuestRun>>> ListRunsByQuestAsync(Guid questId, Guid avatarId, OASISRequest? request = null)
    {
        var owned = await LoadOwnedQuestAsync(questId, avatarId);
        if (owned.IsError || owned.Result == null)
            return new OASISResult<IEnumerable<QuestRun>> { IsError = true, Message = owned.Message };

        return await _runStore.GetByQuestIdAsync(questId);
    }

    public async Task<OASISResult<QuestExecutionState>> GetExecutionStateAsync(Guid runId, Guid avatarId, OASISRequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, avatarId);
        if (runResult.IsError || runResult.Result == null)
            return new OASISResult<QuestExecutionState> { IsError = true, Message = runResult.Message };

        var run = runResult.Result;
        var execsResult = await _executionStore.GetByRunIdAsync(runId);
        var execs = (execsResult.Result ?? Enumerable.Empty<QuestNodeExecution>()).ToList();

        // Counts are derived from rows on every read — no risk of drift.
        // "Pending" here groups Pending + Running as the not-yet-terminal bucket
        // because the API consumer cares about "how many are still in flight".
        var state = new QuestExecutionState
        {
            RunId = run.Id,
            QuestId = run.QuestId,
            Status = run.Status,
            StartedAt = run.StartedAt,
            EndedAt = run.EndedAt,
            TotalNodes = execs.Count,
            CompletedNodes = execs.Count(e => e.State == QuestNodeState.Succeeded),
            FailedNodes = execs.Count(e => e.State == QuestNodeState.Failed),
            PendingNodes = execs.Count(e => e.State == QuestNodeState.Pending || e.State == QuestNodeState.Running),
            NodeExecutions = execs
        };

        return new OASISResult<QuestExecutionState> { Result = state, Message = "Success" };
    }

    public async Task<OASISResult<QuestRun>> MarkRunCompletedAsync(Guid runId, Guid avatarId, OASISRequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, avatarId);
        if (runResult.IsError || runResult.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = runResult.Message };

        var run = runResult.Result;

        // State-machine guard, mirrored from MarkRunFailedAsync: only Running
        // runs may be transitioned to Succeeded/Failed by this supervisor path.
        if (run.Status != QuestRunStatus.Running)
        {
            return new OASISResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot mark run {runId} completed: status is {run.Status} (only Running runs accept supervisor complete)."
            };
        }

        // In-flight guard: every QuestNodeExecution must be terminal before the
        // run can be marked completed. Pending / Running rows indicate the run
        // still has work outstanding — refusing this transition prevents an
        // orchestrator from prematurely closing a run that has live work.
        var execsResult = await _executionStore.GetByRunIdAsync(runId);
        var execs = (execsResult.Result ?? Enumerable.Empty<QuestNodeExecution>()).ToList();
        var inFlight = execs
            .Where(e => e.State == QuestNodeState.Pending || e.State == QuestNodeState.Running)
            .ToList();
        if (inFlight.Count > 0)
        {
            return new OASISResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot mark run {runId} completed: {inFlight.Count} node execution(s) still in flight (Pending/Running)."
            };
        }

        // Terminal status is Failed if any node execution failed, otherwise
        // Succeeded. Mirrors the same derivation used at the end of
        // ExecuteAsync so the supervisor-driven completion produces the same
        // overall verdict as the in-process loop would have.
        run.Status = execs.Any(e => e.State == QuestNodeState.Failed)
            ? QuestRunStatus.Failed
            : QuestRunStatus.Succeeded;
        run.EndedAt = DateTime.UtcNow;
        var updated = await _runStore.UpdateAsync(run);

        return new OASISResult<QuestRun> { Result = updated.Result, Message = $"Run marked {run.Status}." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // DURABLE WORKFLOW ENGINE (durable-workflow-engine)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Start a durable, suspendable workflow run. Mirrors <see cref="ExecuteAsync"/>'s
    /// run + per-node-execution creation (so the existing read surface and
    /// fork/supervisor paths keep working), then — instead of the in-process
    /// node <c>foreach</c> — enqueues the ENTRY node as the first saga step. The
    /// saga processor advances the DAG asynchronously, suspending at
    /// manual/gate/timer nodes (durable-workflow-engine D1, Phase 2).
    /// </summary>
    public async Task<OASISResult<QuestRun>> StartWorkflowRunAsync(Guid questId, Guid avatarId, OASISRequest? request = null)
    {
        var owned = await LoadOwnedQuestAsync(questId, avatarId);
        if (owned.IsError || owned.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = owned.Message };

        var validationResult = await ValidateDAGAsync(questId, request);
        if (validationResult.IsError)
            return new OASISResult<QuestRun> { IsError = true, Message = validationResult.Message };

        var questResult = await _questStore.GetQuestAsync(questId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = questResult.Message };
        var quest = questResult.Result;

        var entry = ResolveEntryNode(quest);
        if (entry is null)
            return new OASISResult<QuestRun> { IsError = true, Message = "Quest has no entry node to start the workflow run." };

        // Create the run + one Pending QuestNodeExecution per node — identical to
        // ExecuteAsync so the per-node claim/idempotency surface is shared.
        var run = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Pending,
            StartedAt = DateTime.UtcNow
        };
        var createRun = await _runStore.CreateAsync(run);
        if (createRun.IsError || createRun.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = createRun.Message };

        foreach (var node in quest.Nodes)
        {
            var exec = new QuestNodeExecution
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                NodeId = node.Id,
                State = QuestNodeState.Pending,
                StartedAt = DateTime.UtcNow
            };
            var createExec = await _executionStore.CreateAsync(exec);
            if (createExec.IsError)
                return new OASISResult<QuestRun> { IsError = true, Message = createExec.Message };
        }

        // Enqueue the entry node as the first saga step (step name = node id).
        // Started directly via the store, not the coordinator, because the
        // quest-workflow definition is not a SagaDefinition (D1, Approach A).
        await EnqueueWorkflowNodeAsync(run.Id, quest.Id, run.AvatarId, entry.Id, signalPayload: null);

        run.Status = QuestRunStatus.Running;
        var updated = await _runStore.UpdateAsync(run);
        return new OASISResult<QuestRun> { Result = updated.Result ?? run, Message = "Workflow run started." };
    }

    /// <summary>
    /// The <c>step(nodeId)</c> primitive: resume a SUSPENDED manual-advance run
    /// from <paramref name="fromNodeId"/> into its single Control successor by
    /// enqueuing that downstream node as a fresh saga step. Avatar-scoped; only
    /// Suspended runs accept advance (mirrors the <see cref="MarkRunCompletedAsync"/>
    /// state-machine guard).
    /// </summary>
    public async Task<OASISResult<QuestRun>> AdvanceAsync(Guid runId, Guid fromNodeId, Guid avatarId, OASISRequest? request = null)
    {
        var runResult = await LoadOwnedRunAsync(runId, avatarId);
        if (runResult.IsError || runResult.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = runResult.Message };
        var run = runResult.Result;

        if (run.Status is not (QuestRunStatus.Suspended or QuestRunStatus.AwaitingSignal))
            return new OASISResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot advance run {runId}: status is {run.Status} (only Suspended/AwaitingSignal runs accept advance)."
            };

        var questResult = await _questStore.GetQuestAsync(run.QuestId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = questResult.Message };
        var quest = questResult.Result;

        var successors = quest.Edges
            .Where(e => e.SourceNodeId == fromNodeId && e.EdgeType == QuestEdgeType.Control)
            .Select(e => e.TargetNodeId)
            .Distinct()
            .ToList();

        if (successors.Count == 0)
        {
            // Manual-advance from a terminal node ⇒ the run completes.
            run.Status = QuestRunStatus.Succeeded;
            run.EndedAt = DateTime.UtcNow;
            var done = await _runStore.UpdateAsync(run);
            return new OASISResult<QuestRun> { Result = done.Result ?? run, Message = "Workflow run completed (advanced past terminal node)." };
        }

        if (successors.Count > 1)
            return new OASISResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot advance: node {fromNodeId} has {successors.Count} Control successors (fan-out is out of scope)."
            };

        await EnqueueWorkflowNodeAsync(run.Id, run.QuestId, run.AvatarId, successors[0], signalPayload: null);
        run.Status = QuestRunStatus.Running;
        var updated = await _runStore.UpdateAsync(run);
        return new OASISResult<QuestRun> { Result = updated.Result ?? run, Message = "Workflow run advanced." };
    }

    /// <summary>
    /// Deliver an external signal to a PARKED gate node: un-park the matching
    /// saga step (G2 single-winner via <c>ISagaStore.TrySignalAsync</c>) so the
    /// processor resumes the gate node, carrying <paramref name="payload"/> into
    /// it. Avatar-scoped; idempotent — a duplicate signal un-parks at most once.
    /// </summary>
    public async Task<OASISResult<QuestRun>> SignalAsync(Guid runId, string gateId, string? payload, Guid avatarId, OASISRequest? request = null)
    {
        if (string.IsNullOrWhiteSpace(gateId))
            return new OASISResult<QuestRun> { IsError = true, Message = "Signal requires a non-empty gateId." };

        var runResult = await LoadOwnedRunAsync(runId, avatarId);
        if (runResult.IsError || runResult.Result == null)
            return new OASISResult<QuestRun> { IsError = true, Message = runResult.Message };
        var run = runResult.Result;

        if (run.Status is not (QuestRunStatus.AwaitingSignal or QuestRunStatus.AwaitingTimer or QuestRunStatus.Suspended))
            return new OASISResult<QuestRun>
            {
                IsError = true,
                Message = $"Cannot signal run {runId}: status is {run.Status} (only a parked/suspended run accepts a signal)."
            };

        var unparked = await _sagaStore.TrySignalAsync(run.Id.ToString(), gateId, CancellationToken.None);
        if (unparked is null)
            return new OASISResult<QuestRun>
            {
                IsError = true,
                Message = $"No node in run {runId} is parked on gate '{gateId}' (already signalled, or wrong gate)."
            };

        // Re-stamp the un-parked step's payload with the signal body so the
        // resumed gate node receives it. TrySignalAsync set the row to Pending;
        // we overwrite its payload via a fresh enqueue-equivalent is unnecessary
        // — instead the resumed node reads SignalPayload from the payload we
        // re-serialize here onto the un-parked record.
        await StampSignalPayloadAsync(unparked, payload);

        run.Status = QuestRunStatus.Running;
        var updated = await _runStore.UpdateAsync(run);
        return new OASISResult<QuestRun> { Result = updated.Result ?? run, Message = "Signal delivered; workflow run resuming." };
    }

    // ── Workflow helpers ───────────────────────────────────────────────────

    /// <summary>The entry node: the explicit <c>IsEntry</c> node, else the
    /// lowest <c>ExecutionOrder</c> node (the validator's topological head).</summary>
    private static QuestNode? ResolveEntryNode(Quest quest)
    {
        var entry = quest.Nodes.FirstOrDefault(n => n.IsEntry);
        if (entry is not null)
            return entry;
        return quest.Nodes.OrderBy(n => n.ExecutionOrder).FirstOrDefault();
    }

    /// <summary>Enqueue a quest node as a saga step (the first step of a run, or
    /// a manual/signal-driven resume). Mirrors the handler's self-advance enqueue
    /// so the correlation key, step name, and idempotency key are derived
    /// identically.</summary>
    private async Task EnqueueWorkflowNodeAsync(
        Guid runId, Guid questId, Guid avatarId, Guid nodeId, string? signalPayload)
    {
        var payload = new QuestStepPayload(runId, questId, avatarId, nodeId, signalPayload);
        var stepName = nodeId.ToString();
        var idemKey = SagaKeys.StepIdempotencyKey(runId.ToString(), stepName);
        await _sagaStore.EnqueueNextStepAsync(
            QuestWorkflowSaga.Name, stepName, runId.ToString(),
            idemKey, SagaStep<QuestStepPayload>.Serialize(payload), CancellationToken.None);
    }

    /// <summary>Re-serialize the un-parked gate step's payload with the delivered
    /// signal body so the resumed node sees <c>SignalPayload != null</c> and
    /// falls through to do its work instead of re-parking.</summary>
    private async Task StampSignalPayloadAsync(SagaStepRecord unparked, string? payload)
    {
        QuestStepPayload? parsed;
        try
        {
            parsed = System.Text.Json.JsonSerializer.Deserialize<QuestStepPayload>(unparked.Payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return; // leave the row as-is; the node re-parks (safe, not lost)
        }
        if (parsed is null)
            return;

        var stamped = parsed with { SignalPayload = payload ?? string.Empty };
        await _sagaStore.UpdateStepPayloadAsync(
            unparked.Id, SagaStep<QuestStepPayload>.Serialize(stamped), CancellationToken.None);
    }
}
