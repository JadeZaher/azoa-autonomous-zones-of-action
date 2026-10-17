using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Managers;

public partial class ActionManager : IFrozenActionRuntime
{
    /// <inheritdoc/>
    public async Task<AZOAResult<ActionRun>> StartAsync(string deploymentId, string invocationKey, RuntimeArtifact artifact,
        Guid actor, Guid? tenant, JsonElement input, bool acknowledgeEconomicEffects, CancellationToken ct = default,
        DeploymentAuthorityWitness? authorityWitness = null,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness? authenticationWitness = null)
    {
        if (!ValidInvocation(deploymentId, invocationKey, artifact, actor, input))
            return AZOAResult<ActionRun>.Failure("Deployment invocation identity, supported runtime and object input are required.");
        var operationId = Guid.ParseExact(CanonicalJson.Hash(new { Domain = "deployed-action-invocation-v1", deploymentId, invocationKey, actor, tenant })[..32], "N");
        var intent = CanonicalJson.Hash(new { deploymentId, artifact, input, acknowledgeEconomicEffects, actor, tenant });
        var replay = await TryReplayAsync(deploymentId, invocationKey, artifact, actor, tenant, input, acknowledgeEconomicEffects, ct);
        if (replay is not null) return replay;
        if (authorityWitness is null) return AZOAResult<ActionRun>.Failure("Deployment authority witnesses are required for a new invocation.");

        var sources = new List<ActionSourceAdmission>();
        foreach (var source in artifact.Sources)
        {
            var (loaded, owner) = await LoadStartableActionAsync(source.ActionId, actor);
            if (loaded.IsError || loaded.Result is null) return AZOAResult<ActionRun>.Failure(loaded.Message);
            var action = loaded.Result;
            if (action.Status != ActionStatus.Active || action.Version != source.Version
                || action.PublishedVersionHash != source.PublishedHash
                || CanonicalJson.Hash(action) != CanonicalJson.Hash(source.Snapshot))
                return AZOAResult<ActionRun>.Failure("A deployment source changed after artifact generation.");
            var consent = EnforceEconomicConsent(action, owner, acknowledgeEconomicEffects);
            if (consent is not null) return consent;
            sources.Add(new ActionSourceAdmission(action.Id, action.Version, action.PublishedVersionHash, _runQuota.EffectiveLimit(owner)));
        }
        var graph = JsonSerializer.Deserialize<ActionDefinition>(JsonSerializer.Serialize(artifact.Graph))
            ?? throw new InvalidOperationException("Deployment graph is unreadable.");
        var structural = _dagValidator.Validate(graph);
        var executable = _executabilityValidator.ValidateFrozen(graph, artifact.OccurrenceBoundaries ?? []);
        var configError = ValidateNodeConfigs(graph);
        if (!structural.IsValid || !executable.IsValid || configError is not null)
            return AZOAResult<ActionRun>.Failure(string.Join("; ", structural.Errors.Concat(executable.Errors).Append(configError).Where(value => value is not null)));
        var blocked = graph.Nodes.FirstOrDefault(node => NotEnabledActionNodeTypes.IsBlocked(node.NodeType));
        if (blocked is not null) return AZOAResult<ActionRun>.Failure(NotEnabledActionNodeTypes.RefusalMessage(blocked.NodeType));
        var dependencies = await EnforceDependenciesAsync(graph, null);
        if (dependencies is not null) return dependencies;
        for (var index = 0; index < structural.TopologicalOrder.Count; index++)
            graph.Nodes.Single(node => node.Id == structural.TopologicalOrder[index]).ExecutionOrder = index;
        var entry = ResolveEntryNode(graph);
        if (entry is null) return AZOAResult<ActionRun>.Failure("Deployment graph has no entry node.");
        var run = new ActionRun
        {
            Id = operationId, ActionId = graph.Id, UserId = actor, ActingTenantId = tenant,
            Status = ActionRunStatus.Running, StartedAt = DateTime.UtcNow, InvocationIdentity = intent,
            InitialInputJson = CanonicalJson.Canonicalize(input),
            AuthenticationWitnessJson = authenticationWitness is null ? null
                : CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authenticationWitness)),
            InternalDependenciesJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(artifact.InternalDependencies ?? [])),
            OccurrenceBoundariesJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(artifact.OccurrenceBoundaries ?? [])),
        };
        ActionRunSnapshot.Capture(run, graph);
        var admission = _workflowAdmission ?? throw new InvalidOperationException("Atomic workflow admission is not registered.");
        return await admission.AdmitAsync(new ActionWorkflowAdmission(run, graph, sources, entry.Id, authorityWitness), ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ActionRun>?> TryReplayAsync(string deploymentId, string invocationKey, RuntimeArtifact artifact,
        Guid actor, Guid? tenant, JsonElement input, bool acknowledgeEconomicEffects, CancellationToken ct = default)
    {
        if (!ValidInvocation(deploymentId, invocationKey, artifact, actor, input))
            return AZOAResult<ActionRun>.Failure("Deployment invocation identity, supported runtime and object input are required.");
        var operationId = Guid.ParseExact(CanonicalJson.Hash(new { Domain = "deployed-action-invocation-v1", deploymentId, invocationKey, actor, tenant })[..32], "N");
        var intent = CanonicalJson.Hash(new { deploymentId, artifact, input, acknowledgeEconomicEffects, actor, tenant });
        var existing = await _runStore.GetByIdAsync(operationId, ct);
        if (existing.Result is not { } replay) return null;
        return replay.UserId == actor && replay.ActingTenantId == tenant && replay.InvocationIdentity == intent
            ? AZOAResult<ActionRun>.Success(replay) : AZOAResult<ActionRun>.Failure("Invocation key conflicts with an existing run.");
    }

    private static bool ValidInvocation(string deploymentId, string invocationKey, RuntimeArtifact? artifact, Guid actor, JsonElement input)
        => actor != Guid.Empty && !string.IsNullOrWhiteSpace(deploymentId) && !string.IsNullOrWhiteSpace(invocationKey)
            && invocationKey.Length <= 200 && artifact is { RuntimeVersion: "azoa-actions/2", Sources.Count: > 0 }
            && input.ValueKind == JsonValueKind.Object;
}
