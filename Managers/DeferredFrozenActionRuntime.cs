using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

/// <summary>Forwards to the scope's <see cref="ActionManager"/> per call, breaking the ActionManager → deployment resolver → DeploymentService DI cycle; see <c>Managers/AGENTS.md</c> §DeferredFrozenActionRuntime.</summary>
public sealed class DeferredFrozenActionRuntime(IServiceProvider scope) : IFrozenActionRuntime
{
    /// <inheritdoc/>
    public Task<AZOAResult<ActionRun>> StartAsync(string deploymentId, string invocationKey, RuntimeArtifact artifact,
        Guid actor, Guid? tenant, JsonElement input, bool acknowledgeEconomicEffects, CancellationToken ct = default,
        DeploymentAuthorityWitness? authorityWitness = null,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness? authenticationWitness = null)
        => Runtime.StartAsync(deploymentId, invocationKey, artifact, actor, tenant, input, acknowledgeEconomicEffects, ct,
            authorityWitness, authenticationWitness);

    /// <inheritdoc/>
    public Task<AZOAResult<ActionRun>?> TryReplayAsync(string deploymentId, string invocationKey, RuntimeArtifact artifact,
        Guid actor, Guid? tenant, JsonElement input, bool acknowledgeEconomicEffects, CancellationToken ct = default)
        => Runtime.TryReplayAsync(deploymentId, invocationKey, artifact, actor, tenant, input, acknowledgeEconomicEffects, ct);

    private IFrozenActionRuntime Runtime => scope.GetRequiredService<ActionManager>();
}
