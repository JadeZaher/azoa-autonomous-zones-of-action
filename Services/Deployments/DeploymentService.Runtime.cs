using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using ActionRun = AZOA.WebAPI.Models.Actions.ActionRun;

namespace AZOA.WebAPI.Services.Deployments;

public sealed partial class DeploymentService
{
    /// <inheritdoc/>
    public async Task<AZOAResult<ActionRun>> InvokeAsync(string operationId, Guid actor, Guid? tenant,
        string invocationKey, DeploymentInvokeRequest request, CancellationToken ct = default,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness? authenticationWitness = null)
    {
        if (string.IsNullOrWhiteSpace(invocationKey) || invocationKey.Length > 200 || invocationKey.Any(char.IsControl))
            return Fail<ActionRun>("Invocation requires an Idempotency-Key.");
        var loaded = await LoadRuntimeAsync(operationId, actor, tenant, ct);
        if (loaded is null) return Fail<ActionRun>("Active deployment not found for this caller.");
        var (artifact, payload) = loaded.Value;
        if (artifact.PayloadHash != request.ExpectedPayloadHash) return Fail<ActionRun>("The executable artifact does not match this deployment.");
        var replay = await runtime.TryReplayAsync(operationId, invocationKey, payload.Runtime!, actor, tenant,
            request.Input, request.AcknowledgeEconomicEffects, ct);
        if (replay is not null) return replay;
        var permitted = await CheckRuntimeAsync(payload.Runtime!, actor, ct);
        if (permitted is not null) return Fail<ActionRun>(permitted);
        var authority = await RuntimeAuthorityAsync(payload, actor, ct);
        if (authority.IsError || authority.Result is null) return Fail<ActionRun>(authority.Message);
        return await runtime.StartAsync(operationId, invocationKey, payload.Runtime!, actor, tenant,
            request.Input, request.AcknowledgeEconomicEffects, ct, authorityWitness: authority.Result,
            authenticationWitness: authenticationWitness);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<string>> GetLoaderAsync(string operationId, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        var loaded = await LoadRuntimeAsync(operationId, actor, tenant, ct);
        if (loaded is null) return Fail<string>("Active deployment not found for this caller.");
        var permitted = await CheckRuntimeAsync(loaded.Value.Payload.Runtime!, actor, ct);
        if (permitted is not null) return Fail<string>(permitted);
        var authority = await RuntimeAuthorityAsync(loaded.Value.Payload, actor, ct);
        if (authority.IsError) return Fail<string>(authority.Message);
        return AZOAResult<string>.Success(loaded.Value.Artifact.Loader ?? throw new InvalidOperationException("Executable loader is missing."));
    }

    private async Task<(DeploymentArtifact Artifact, DeploymentArtifactPayload Payload)?> LoadRuntimeAsync(
        string id, Guid actor, Guid? tenant, CancellationToken ct)
    {
        var row = await store.GetAsync(id, ct);
        if (row is null || row.State != "Confirmed" || row.TenantId != Tenant(tenant) || actor == Guid.Empty) return null;
        var artifact = await store.GetArtifactAsync(row.ArtifactId, ct);
        if (artifact is null || artifact.ArtifactHash != row.ArtifactHash) throw new InvalidOperationException("Registered artifact identity changed.");
        var payload = Read<DeploymentArtifactPayload>(artifact.PayloadJson);
        if (payload.Version != 2 || payload.Runtime is null || payload.Target.Kind != "PlatformRuntime") return null;
        return (artifact, payload);
    }

    private async Task<AZOAResult<DeploymentAuthorityWitness>> RuntimeAuthorityAsync(DeploymentArtifactPayload payload, Guid actor, CancellationToken ct)
    {
        var bundleWitnesses = new List<DeploymentBundleWitness>();
        var root = await bundles.GetByIdAsync(payload.BundleId, ct);
        if (root.IsError || root.Result is null || !root.Result.IsActive || root.Result.UserId != actor && !root.Result.IsPublic)
            return Fail<DeploymentAuthorityWitness>("The deployed bundle is no longer available to this caller.");
        bundleWitnesses.Add(new(payload.BundleId, root.Result.UserId, root.Result.IsPublic, null));
        foreach (var reference in payload.Runtime!.BundleReferences ?? [])
        {
            var nested = await bundles.GetByIdAsync(reference.BundleId, ct);
            if (nested.IsError || nested.Result is null || !nested.Result.IsActive || nested.Result.UserId != actor && !nested.Result.IsPublic
                || !TryBundleArtifact(nested.Result.DeploymentConfig, out var hash) || hash != reference.ArtifactHash)
                return Fail<DeploymentAuthorityWitness>("A nested bundle revision or current permission changed.");
            bundleWitnesses.Add(new(reference.BundleId, nested.Result.UserId, nested.Result.IsPublic, nested.Result.DeploymentConfig));
        }
        var resources = new List<DeploymentHolonWitness>();
        foreach (var reference in payload.Runtime.References)
        {
            var current = await holons.GetAsync(reference.HolonId, actor);
            if (current.IsError || current.Result is null || ReferenceHash(current.Result) != reference.ContentHash)
                return Fail<DeploymentAuthorityWitness>("A bound resource changed before invocation.");
            resources.Add(ResourceWitness(current.Result));
        }
        return AZOAResult<DeploymentAuthorityWitness>.Success(new(bundleWitnesses, resources));
    }
}
