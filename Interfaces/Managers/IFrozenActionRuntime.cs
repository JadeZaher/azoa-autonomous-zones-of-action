using System.Text.Json;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Starts the existing action interpreter with an immutable deployed graph and the current caller's authority.</summary>
public interface IFrozenActionRuntime
{
    /// <summary>Checks every source's live access, consent and quotas before idempotently admitting the saved graph.</summary>
    Task<AZOAResult<ActionRun>> StartAsync(string deploymentId, string invocationKey, RuntimeArtifact artifact,
        Guid actor, Guid? tenant, JsonElement input, bool acknowledgeEconomicEffects, CancellationToken ct = default,
        DeploymentAuthorityWitness? authorityWitness = null,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness? authenticationWitness = null);
    /// <summary>Returns a matching admitted invocation without rechecking authority to perform a new effect.</summary>
    Task<AZOAResult<ActionRun>?> TryReplayAsync(string deploymentId, string invocationKey, RuntimeArtifact artifact,
        Guid actor, Guid? tenant, JsonElement input, bool acknowledgeEconomicEffects, CancellationToken ct = default);
}
