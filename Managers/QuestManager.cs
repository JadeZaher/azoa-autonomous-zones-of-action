using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;
using OASIS.WebAPI.Services.Quest;

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

    public QuestManager(
        IQuestStore questStore,
        IQuestRunStore runStore,
        IQuestNodeExecutionStore executionStore,
        IQuestDagValidator dagValidator,
        IQuestNodeHandlerRegistry registry)
    {
        _questStore = questStore;
        _runStore = runStore;
        _executionStore = executionStore;
        _dagValidator = dagValidator;
        _registry = registry;
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

    public async Task<OASISResult<Quest>> GetAsync(Guid id, OASISRequest? request = null)
    {
        return await _questStore.GetQuestAsync(id);
    }

    public async Task<OASISResult<IEnumerable<Quest>>> GetByAvatarAsync(Guid avatarId, OASISRequest? request = null)
    {
        return await _questStore.GetQuestsByAvatarAsync(avatarId);
    }

    public async Task<OASISResult<Quest>> UpdateAsync(Guid id, QuestUpdateModel model, OASISRequest? request = null)
    {
        var existing = await _questStore.GetQuestAsync(id);
        if (existing.IsError || existing.Result == null) return existing;

        var quest = existing.Result;
        if (model.Name != null) quest.Name = model.Name;
        if (model.Description != null) quest.Description = model.Description;
        // model.Status is intentionally ignored — runtime status moved to
        // QuestRun.Status (see ADR §2.2). The field on QuestUpdateModel is
        // retained for API back-compat but has no effect on the definition.

        return await _questStore.UpsertQuestAsync(quest);
    }

    public async Task<OASISResult<bool>> DeleteAsync(Guid id, OASISRequest? request = null)
    {
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

    public async Task<OASISResult<QuestRun>> ExecuteAsync(Guid questId, OASISRequest? request = null)
    {
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

    public async Task<OASISResult<QuestNodeExecution>> ExecuteNodeAsync(Guid questId, Guid nodeId, OASISRequest? request = null)
    {
        var questResult = await _questStore.GetQuestAsync(questId);
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

    public async Task<OASISResult<QuestRun>> ForkAsync(Guid runId, Guid atNodeId, string reason, OASISRequest? request = null)
    {
        var parentResult = await _runStore.GetByIdAsync(runId);
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

    public async Task<OASISResult<QuestRun>> MarkRunFailedAsync(Guid runId, string reason, OASISRequest? request = null)
    {
        var runResult = await _runStore.GetByIdAsync(runId);
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
}
