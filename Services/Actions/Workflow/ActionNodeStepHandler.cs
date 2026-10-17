using System.Text.Json;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Services.Actions.Workflow;

/// <summary>Admits immutable node inputs and resumes durable domain effects before advancing.</summary>
public sealed class ActionNodeStepHandler : IStepHandler<ActionStepPayload>, IValueNodePauseSeam
{
    /// <inheritdoc />
    public bool IsAccountPauseEnforced => _railGate.IsAccountPauseEnforced;

    private readonly IActionStore _actionStore;
    private readonly IActionRunStore _runStore;
    private readonly IActionNodeExecutionStore _executionStore;
    private readonly IActionNodeHandlerRegistry _registry;
    private readonly ActionConfigBindingResolver _bindingResolver;
    private readonly ILogger<ActionNodeStepHandler> _logger;
    private readonly ValueRailCapabilityGate _railGate;
    private readonly ActionOperationRecovery _operationRecovery;
    private readonly IActionWorkflowAdmissionStore? _workflowAdmission;
    private readonly SigningAdmissionAuthorityFactory? _signingAuthority;

    public ActionNodeStepHandler(
        IActionStore actionStore,
        IActionRunStore runStore,
        IActionNodeExecutionStore executionStore,
        IActionNodeHandlerRegistry registry,
        ISagaStore sagaStore,
        IWalletManager walletManager,
        IBlockchainProviderFactory chainFactory,
        ActionConfigBindingResolver bindingResolver,
        ILogger<ActionNodeStepHandler> logger,
        IEnumerable<IRailDestinationDirectory>? railDestinationDirectories = null,
        AZOA.WebAPI.Services.Fiat.Reversal.IFiatReversalLedger? accountPauseLedger = null,
        IEnumerable<IActionExternalOperationResolver>? operationResolvers = null,
        IActionWorkflowAdmissionStore? workflowAdmission = null,
        SigningAdmissionAuthorityFactory? signingAuthority = null)
    {
        _actionStore = actionStore;
        _runStore = runStore;
        _executionStore = executionStore;
        _registry = registry;
        _bindingResolver = bindingResolver;
        _logger = logger;
        _operationRecovery = new ActionOperationRecovery(operationResolvers ?? []);
        _workflowAdmission = workflowAdmission;
        _signingAuthority = signingAuthority;
        _railGate = new ValueRailCapabilityGate(
            walletManager, chainFactory.DefaultRailId, railDestinationDirectories, accountPauseLedger);
    }

    /// <inheritdoc/>
    public async Task<StepResult> ExecuteAsync(
        StepExecutionContext<ActionStepPayload> ctx, CancellationToken ct)
    {
        var p = ctx.Payload;

        var runResult = await _runStore.GetByIdAsync(p.RunId, ct);
        if (runResult.IsError || runResult.Result is null)
            return StepResult.Fail($"Run {p.RunId} is unavailable: {runResult.Message}");
        var run = runResult.Result;
        if (run.Status.IsTerminal()) return run.Status == ActionRunStatus.Succeeded
            ? StepResult.Ok(null) : StepResult.Fail("The action run is terminal.");
        Models.Actions.ActionDefinition action;
        if (run.DefinitionSnapshotJson is not null)
            action = ActionRunSnapshot.Read(run);
        else
        {
            var legacy = await _actionStore.GetActionAsync(p.ActionId, ct);
            if (legacy.IsError || legacy.Result is null) return StepResult.Fail("Legacy action graph is unavailable.");
            action = legacy.Result;
        }

        var node = action.Nodes.FirstOrDefault(n => n.Id == p.NodeId);
        if (node is null)
            return StepResult.Fail(
                $"Node {p.NodeId} not found in action {p.ActionId} (run {p.RunId}).");

        var (advance, marker) = WorkflowNodeConfig.Parse(node.Config);
        var prior = await _executionStore.GetByRunAndNodeAsync(p.RunId, p.NodeId, ct);
        if (prior.IsError || prior.Result is null) return StepResult.Fail("Node execution is unavailable.");
        if (prior.Result.State != ActionNodeState.Pending)
            return await ReplayAdvancementAsync(action, node, p, advance, ct);
        if (run.DefinitionSnapshotJson is null)
            return await ParkForOperatorAsync(prior.Result, "Legacy run has no frozen graph; admission requires operator evidence.", ct);

        var admission = _workflowAdmission ?? throw new InvalidOperationException("Atomic workflow admission is not registered.");
        var dag = await new ActionDagScheduler(_runStore, _executionStore, admission).AdvanceAsync(action, run.Id, ct);
        if (dag.DecisionChanged) return StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(2));
        if (dag.UnhandledFailure) return dag.HasAdmittedEffects
            ? StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(20))
            : StepResult.Fail("An action node failed without a recovery branch.");
        prior = await _executionStore.GetByRunAndNodeAsync(p.RunId, p.NodeId, ct);
        if (prior.IsError || prior.Result is null) return StepResult.Fail("Node execution is unavailable.");
        if (prior.Result.State != ActionNodeState.Pending)
            return await ReplayAdvancementAsync(action, node, p, advance, ct);
        var predecessors = await _executionStore.GetByRunIdAsync(run.Id, ct);
        if (predecessors.IsError || predecessors.Result is null) return StepResult.Fail(predecessors.Message);
        var before = predecessors.Result.ToDictionary(item => item.NodeId);
        if (ActionDagScheduler.OccurrenceState(run, node.Id, action, before) != ActionOccurrenceState.Ready)
            return StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(20));
        if (action.Edges.Where(edge => edge.TargetNodeId == node.Id).Any(edge =>
            !before.TryGetValue(edge.SourceNodeId, out var source)
            || source.State is ActionNodeState.Pending or ActionNodeState.Running
            || (source.State == ActionNodeState.Succeeded && !source.ManualAdvanceReleased
                && WorkflowNodeConfig.Parse(action.Nodes.Single(item => item.Id == edge.SourceNodeId).Config).Item1 == WorkflowAdvance.Manual)))
            return StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(20));

        if (advance == WorkflowAdvance.Timer)
        {
            if (prior.Result.WaitUntil is null)
            {
                prior.Result.WaitUntil = DateTime.UtcNow.AddSeconds(Math.Max(1, marker?.ResumeInSeconds ?? 0));
                var armed = await _executionStore.UpdateAsync(prior.Result, ActionNodeState.Pending, ct);
                if (armed.IsError) return StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(1));
                prior = armed;
            }
            if (prior.Result!.WaitUntil > DateTime.UtcNow)
            {
                await ProjectRunStatusAsync(p.RunId, ActionRunStatus.AwaitingTimer, ct);
                return StepResult.Parked(string.Empty, prior.Result.WaitUntil);
            }
        }
        else if (advance == WorkflowAdvance.Gated && p.SignalPayload is null)
        {
            await ProjectRunStatusAsync(p.RunId, ActionRunStatus.AwaitingSignal, ct);
            return StepResult.Parked(marker?.GateId ?? node.Id.ToString());
        }

        var (upstream, allRunExecutions) = await LoadRunExecutionsAsync(action, p.NodeId, p.RunId, ct);
        IReadOnlyList<DeploymentInternalDependency> dependencies = run.InternalDependenciesJson is null ? []
            : JsonSerializer.Deserialize<List<DeploymentInternalDependency>>(run.InternalDependenciesJson)
                ?? throw new InvalidOperationException("Saved internal dependencies are unreadable.");
        var requiredIds = dependencies.Where(item => item.EntryNodeId == p.NodeId && item.DependencyType == ActionDependencyType.Required)
            .SelectMany(item => item.RequiredNodeIds).ToArray();
        if (requiredIds.Any(id => !allRunExecutions.TryGetValue(id, out var required)
            || required.State is ActionNodeState.Pending or ActionNodeState.Running))
            return StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(20));
        if (requiredIds.Any(id => allRunExecutions[id].State != ActionNodeState.Succeeded))
        {
            prior.Result.State = ActionNodeState.Skipped;
            prior.Result.Error = "A required frozen deployment dependency did not succeed.";
            prior.Result.EndedAt = DateTime.UtcNow;
            await _executionStore.UpdateAsync(prior.Result, ActionNodeState.Pending, ct);
            return await ReplayAdvancementAsync(action, node, p, advance, ct);
        }
        using var invocationInput = run.InitialInputJson is null ? null : JsonDocument.Parse(run.InitialInputJson);
        var savedInput = prior.Result.FrozenInput is null ? null : JsonSerializer.Deserialize<ActionFrozenNodeInput>(prior.Result.FrozenInput);
        if (savedInput is not null && (savedInput.Version != 1 || savedInput.RunId != run.Id || savedInput.NodeId != node.Id
            || savedInput.ActorUserId != run.UserId || savedInput.ActingTenantId != run.ActingTenantId || savedInput.NodeType != node.NodeType))
            return await ParkForOperatorAsync(prior.Result, "Frozen node identity does not match the workflow.", ct);
        var binding = savedInput is null ? await _bindingResolver.TryResolveAsync(node.Config, node, action, upstream,
            allRunExecutions, run.UserId, ct, invocationInput?.RootElement)
            : new ActionConfigBindingResolver.BindingResolveResult(true, savedInput.Config.GetRawText(), null);
        var originalConfig = node.Config;
        ActionNodeHandlerResult? refusal = null;
        _registry.TryGetFor(node, out var handler);
        if (handler is null) refusal = ActionNodeHandlerResult.Invalid($"Unsupported node type: {node.NodeType}");
        else if (NotEnabledActionNodeTypes.IsBlocked(node.NodeType))
            refusal = ActionNodeHandlerResult.Invalid(NotEnabledActionNodeTypes.RefusalMessage(node.NodeType));
        else if (!binding.Ok) refusal = ActionNodeHandlerResult.Invalid($"Node input binding failed: {binding.Error}");
        var handlerRequiresChain = handler?.RequiresChainCapability == true;
        node.Config = binding.Ok ? binding.ResolvedJson! : originalConfig;
        ActionNodeExecution execution;
        ActionNodeHandlerResult result;
        try
        {
            if (refusal is null && handlerRequiresChain
                && await _railGate.AdmitValueNodeAsync(run.UserId, handler!.ResolveSettlementRailId(node), ct) is { IsAllowed: false } denied)
                refusal = ActionNodeHandlerResult.Invalid(denied.RefusalMessage);
            using var config = JsonDocument.Parse(binding.Ok ? (string.IsNullOrWhiteSpace(node.Config) ? "null" : node.Config) : JsonSerializer.Serialize(new { InvalidConfig = node.Config }));
            var input = savedInput ?? new ActionFrozenNodeInput(1, p.RunId, p.NodeId, node.NodeType, run.UserId,
                run.ActingTenantId, config.RootElement.Clone(), upstream, allRunExecutions);
            var canonical = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input));
            var nodeContext = new ActionNodeExecutionContext(p.RunId, p.NodeId, action, run.UserId,
                input.Upstream, run.ActingTenantId, input.AllExecutions);
            var candidate = prior.Result;
            candidate.FrozenInput = canonical;
            candidate.ExternalOperation = refusal is null && handler is IRecoverableActionNodeHandler recoverable
                ? recoverable.CreateReference(nodeContext, CanonicalJson.HashCanonical(canonical)) : null;
            candidate.AdmissionRejection = refusal?.Message;
            if (refusal is null)
            {
                var scopes = handler!.ResolveSigningScopes(node);
                if (scopes is null)
                    return await ParkForParticipantAsync(candidate, "The frozen node has no supported signing authorization contract.", ct);
                if (scopes.Count > 0 && candidate.SigningAuthorityJson is null)
                {
                    var credential = run.AuthenticationWitnessJson is null ? null
                        : JsonSerializer.Deserialize<SigningCredentialWitness>(run.AuthenticationWitnessJson);
                    if (credential is null || _signingAuthority is null)
                        return await ParkForParticipantAsync(candidate, "This workflow has no original credential evidence. The participant must authorize this exact frozen node.", ct);
                    var selected = await _signingAuthority.SelectWorkflowAsync(credential,
                        new(p.RunId, p.NodeId, run.UserId, run.ActingTenantId, CanonicalJson.HashCanonical(canonical)), scopes, ct);
                    if (selected.IsError || selected.Result is null)
                        return await ParkForParticipantAsync(candidate, selected.Message, ct);
                    candidate.SigningAuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(selected.Result));
                }
            }
            candidate.RecoveryState = "Admitted";
            var claim = await _executionStore.TryAdmitAsync(candidate, ct, dag.RunVersion);
            if (claim.IsError) return StepResult.Fail(claim.Message);
            if (claim.Result is null) return await ReplayAdvancementAsync(action, node, p, advance, ct);
            execution = claim.Result;
            result = refusal ?? await handler!.HandleAsync(nodeContext, ct);
        }
        finally { node.Config = originalConfig; }

        if (result.PendingOperation is { } pending)
            return await RecordObservationAsync(action, node, p, advance, execution, pending, ct);

        if (handlerRequiresChain && !string.IsNullOrWhiteSpace(result.TxHash)
            && execution.State == ActionNodeState.Running)
        {
            execution.TxHash = result.TxHash;
            execution.ChainType = result.ChainType;
            var stamped = await _executionStore.UpdateAsync(
                execution, expectedState: ActionNodeState.Running, ct);
            if (stamped.IsError)
                return await ReplayAdvancementAsync(action, node, p, advance, ct);
        }

        if (result.IsError && handlerRequiresChain)
        {
            var reconciled = await ReconcileChainFailureAsync(
                action, node, p, advance, execution, result, ct);
            if (reconciled is { } outcome)
                return outcome;
        }

        // ── 4. Record terminal execution state (guarded on Running) ───────────
        if (result.IsError)
        {
            execution.State = ActionNodeState.Failed;
            execution.Error = result.Message;
        }
        else
        {
            execution.State = ActionNodeState.Succeeded;
            execution.Output = result.Output;
            execution.TxHash = result.TxHash;
            execution.ChainType = result.ChainType;
        }
        execution.EndedAt = DateTime.UtcNow;
        var recorded = await _executionStore.UpdateAsync(
            execution, expectedState: ActionNodeState.Running, ct);
        if (recorded.IsError)
        {
            return await ReplayAdvancementAsync(action, node, p, advance, ct);
        }

        return await AdvanceAsync(action, node, p, advance, result.Output, ct);
    }

    private async Task<StepResult> AdvanceAsync(
        Models.Actions.ActionDefinition action, ActionNode node, ActionStepPayload payload,
        WorkflowAdvance advance, string? output, CancellationToken ct)
    {
        var admission = _workflowAdmission ?? throw new InvalidOperationException("Atomic workflow admission is not registered.");
        var progress = await new ActionDagScheduler(_runStore, _executionStore, admission).AdvanceAsync(action, payload.RunId, ct);
        if (progress.DecisionChanged) return StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(2));
        if (!progress.UnhandledFailure) return StepResult.Ok(output);
        return progress.HasAdmittedEffects ? StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(20))
            : StepResult.Fail("An action node failed without a recovery branch.");
    }

    private async Task<StepResult?> ReconcileChainFailureAsync(
        Models.Actions.ActionDefinition action, ActionNode node, ActionStepPayload p,
        WorkflowAdvance advance, ActionNodeExecution execution,
        ActionNodeHandlerResult result, CancellationToken ct)
    {
        if (!result.Retriable) return null;
        if (execution.ExternalOperation is not null)
        {
            var run = await _runStore.GetByIdAsync(p.RunId, ct);
            if (run.IsError || run.Result is null) return StepResult.Fail("Run is unavailable during recovery.");
            var observation = await _operationRecovery.ObserveAsync(execution, run.Result, ct);
            return await RecordObservationAsync(action, node, p, advance, execution, observation, ct);
        }
        execution.TxHash = result.TxHash;
        execution.ChainType = result.ChainType;
        return await ParkForOperatorAsync(execution,
            "The failed effect lacks authoritative domain correlation; transaction confirmation alone cannot prove its semantics.", ct);
    }

    private async Task<StepResult> ReplayAdvancementAsync(
        Models.Actions.ActionDefinition action, ActionNode node, ActionStepPayload p,
        WorkflowAdvance advance, CancellationToken ct)
    {
        var existing = await _executionStore.GetByRunAndNodeAsync(p.RunId, p.NodeId, ct);
        var state = existing.Result?.State;

        if (existing.IsError || existing.Result is null)
            return StepResult.Fail("Node execution disappeared during recovery.");
        if (state == ActionNodeState.Pending)
            return StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(2));
        if (state == ActionNodeState.Running)
        {
            if (existing.Result.AdmissionRejection is { } rejected)
            {
                existing.Result.State = ActionNodeState.Failed;
                existing.Result.Error = rejected;
                existing.Result.EndedAt = DateTime.UtcNow;
                var failed = await _executionStore.UpdateAsync(existing.Result, ActionNodeState.Running, ct);
                return failed.IsError ? StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(2))
                    : await AdvanceAsync(action, node, p, advance, null, ct);
            }
            if (existing.Result.ExternalOperation is null)
                return await ParkForOperatorAsync(existing.Result, "Execution has no durable domain receipt or correlation.", ct);
            var run = await _runStore.GetByIdAsync(p.RunId, ct);
            if (run.IsError || run.Result is null) return StepResult.Fail("Run is unavailable during recovery.");
            var observation = await _operationRecovery.ObserveAsync(existing.Result, run.Result, ct);
            return await RecordObservationAsync(action, node, p, advance, existing.Result, observation, ct);
        }
        if (state is ActionNodeState.Cancelled)
            return StepResult.Fail("Node execution was cancelled.");

        // Succeeded/Skipped ⇒ re-drive forward advancement idempotently.
        _logger.LogInformation(
            "Action workflow: node {NodeId} (run {RunId}) already {State} — " +
            "idempotent replay, re-driving advancement.", p.NodeId, p.RunId, state);
        return await AdvanceAsync(action, node, p, advance, existing.Result?.Output, ct);
    }

    private async Task<StepResult> ParkForOperatorAsync(ActionNodeExecution execution, string detail, CancellationToken ct)
    {
        execution.RecoveryState = "NeedsOperator";
        execution.Error = detail;
        var stored = await _executionStore.UpdateAsync(execution, execution.State, ct);
        if (stored.IsError) return StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(20));
        await ProjectRunStatusAsync(execution.RunId, ActionRunStatus.AwaitingReconciliation, ct);
        return StepResult.Parked($"operator:{execution.NodeId:N}", null);
    }

    private async Task<StepResult> ParkForParticipantAsync(ActionNodeExecution execution, string detail, CancellationToken ct)
    {
        execution.RecoveryState = "NeedsParticipantAuthorization";
        execution.Error = detail;
        var saved = await _executionStore.UpdateAsync(execution, ActionNodeState.Pending, ct);
        if (saved.IsError) return StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(2));
        await ProjectRunStatusAsync(execution.RunId, ActionRunStatus.AwaitingReconciliation, ct);
        return StepResult.Parked($"participant:{execution.NodeId:N}", null);
    }

    private async Task<StepResult> RecordObservationAsync(Models.Actions.ActionDefinition action, ActionNode node,
        ActionStepPayload payload, WorkflowAdvance advance, ActionNodeExecution execution,
        ActionExternalOperationObservation observation, CancellationToken ct)
    {
        if (execution.ExternalOperation is null || !ActionOperationIdentity.Matches(execution.ExternalOperation, observation.Reference))
            return await ParkForOperatorAsync(execution, "Observation does not match the admitted domain identity.", ct);
        if (observation.Status == ActionExternalOperationStatus.Ambiguous)
            return await ParkForOperatorAsync(execution, observation.Detail ?? "Domain evidence needs operator recovery.", ct);
        execution.ExternalOperation = observation.Reference;
        execution.Output = observation.Output;
        execution.TxHash = observation.TransactionId;
        execution.ChainType = observation.ChainType;
        execution.Error = observation.Detail;
        execution.RecoveryState = observation.Status.ToString();
        if (observation.Status != ActionExternalOperationStatus.Pending)
        {
            execution.State = observation.Status == ActionExternalOperationStatus.Succeeded ? ActionNodeState.Succeeded
                : observation.Status == ActionExternalOperationStatus.Cancelled ? ActionNodeState.Cancelled : ActionNodeState.Failed;
            execution.EndedAt = DateTime.UtcNow;
        }
        var stored = await _executionStore.UpdateAsync(execution, ActionNodeState.Running, ct);
        if (stored.IsError) return StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(20));
        if (observation.Status == ActionExternalOperationStatus.Pending)
        {
            await ProjectRunStatusAsync(execution.RunId, ActionRunStatus.AwaitingReconciliation, ct);
            return StepResult.Parked(string.Empty, DateTime.UtcNow.AddSeconds(20));
        }
        return await ReplayAdvancementAsync(action, node, payload, advance, ct);
    }

    /// <summary>
    /// Loads, in one store round-trip, both the direct-upstream execution map
    /// (incoming-edge sources, for the <c>upstream.</c> root) and the full
    /// run-execution map keyed by node id (for the run-scoped <c>run.</c> root).
    /// See Services/Actions/AGENTS.md §output-binding.
    /// </summary>
    private async Task<(IReadOnlyDictionary<Guid, ActionNodeExecution> Upstream,
                        IReadOnlyDictionary<Guid, ActionNodeExecution> AllByNode)>
        LoadRunExecutionsAsync(Models.Actions.ActionDefinition action, Guid nodeId, Guid runId, CancellationToken ct)
    {
        var upstream = new Dictionary<Guid, ActionNodeExecution>();

        var all = await _executionStore.GetByRunIdAsync(runId, ct);
        if (all.IsError || all.Result is null)
            return (upstream, new Dictionary<Guid, ActionNodeExecution>());

        var byNode = all.Result.ToDictionary(e => e.NodeId);
        foreach (var edge in action.Edges.Where(e => e.TargetNodeId == nodeId))
        {
            if (byNode.TryGetValue(edge.SourceNodeId, out var exec))
                upstream[edge.SourceNodeId] = exec;
        }
        return (upstream, byNode);
    }

    /// <summary>
    /// Persist the <see cref="ActionRun.Status"/> read-model projection. Derived
    /// from saga-step transitions (the saga rows are the source of truth, D7) and
    /// idempotent: a non-terminal run can re-project on replay; a run already in
    /// a terminal state is never regressed.
    /// </summary>
    private async Task ProjectRunStatusAsync(Guid runId, ActionRunStatus status, CancellationToken ct)
    {
        var runResult = await _runStore.GetByIdAsync(runId, ct);
        if (runResult.IsError || runResult.Result is null)
            return;
        var run = runResult.Result;

        if (run.Status.IsTerminal())
            return; // never regress a terminal run

        if (run.Status == status)
            return; // idempotent no-op

        var expected = run.Status;
        run.Status = status;
        if (status.IsTerminal())
            run.EndedAt = DateTime.UtcNow;
        await _runStore.UpdateAsync(run, expectedStatus: expected, ct);
    }
}
