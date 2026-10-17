using System.Text.Json;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Owner-takeover and exposed-participant exits; see Services/Wormhole/AGENTS.md §Operation exits.</summary>
public sealed partial class SurrealWttOperationStore
{
    /// <inheritdoc/>
    public async Task<WttOperation?> TakeOverAsync(string operationId, long expectedVersion, string ownerIntentJson,
        WttStageAuthority authority, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        if (operation is null || operation.Version != expectedVersion || operation.TenantId.Length == 0 || operation.OwnerIntentJson is not null
            || operation.ActionRunId is not null || operation.Phase != "Target" || operation.State != "Ready" || operation.ProofsJson is null)
            return null;
        var original = JsonSerializer.Deserialize<WttOperationIntent>(operation.IntentJson)!;
        var owner = WttOwnerIntent.From(original);
        if (ownerIntentJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(owner))) return null;
        // The version pins the stage set: a target attempt signed or disclosed under the tenant keeps its original authority.
        if ((await ReadStagesAsync(operationId, ct)).Any(stage => stage.Phase is "Target" or "NativeOutput"
                && (stage.AuthorizedAt is not null || stage.ExposedAt is not null)))
            return null;
        var guards = WttSigningAuthorityFence.Stage(owner, "Target", null, authority, "wtt_fence_takeover");
        if (guards is null) return null;
        var now = Now;
        var mutation = SurrealWriter.UpdateOnly<WttOperation>(operationId)
            .Where(row => row.Version == expectedVersion && row.State == "Ready" && row.Phase == "Target" && row.OwnerIntentJson == null)
            .Set(row => row.OwnerIntentJson, ownerIntentJson)
            .Set(row => row.Detail, "The owner took over target signing from the tenant credential.")
            .Set(row => row.NextAttemptAt, now).Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, now)
            .Unset(row => row.TargetQuoteJson).Build();
        return await CommitOperationAsync(guards, mutation, ct) ? await GetAsync(operationId, ct) : null;
    }

    /// <inheritdoc/>
    public async Task<WttOperationStage?> ResolveExposedAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, string observationJson, string verdict, string? participantPreparedJson = null,
        CancellationToken ct = default)
    {
        var (operation, stage) = await CurrentAsync(operationId, stageId, ct);
        if (operation is null || stage is null || !Canonical(observationJson, null) || stage.ExposedAt is null) return null;
        if (stage.State != "Exposed") return stage.ObservationJson == observationJson ? stage : null;
        if (operation.Version != expectedVersion || operation.State != "Active" || stage.Version != expectedStageVersion
            || stage.AuthorizedAt is not null || verdict is not ("Pending" or "ExpiredNoEffect" or "Landed")) return null;
        if (verdict == "Landed" && (participantPreparedJson is null || !Canonical(participantPreparedJson, null)
                || stage.PreparedJson is not null && stage.PreparedJson != participantPreparedJson))
            return null;
        if (verdict == "Pending" && stage.ObservationJson == observationJson) return stage;
        var now = Now;
        var stageVersion = stage.Version;
        var stageMutation = SurrealWriter.UpdateOnly<WttOperationStage>(stageId)
            .Where(row => row.Version == stageVersion && row.State == "Exposed" && row.AuthorizedAt == null && row.ExposedAt != null)
            .Set(row => row.ObservationJson, observationJson).Set(row => row.Version, stageVersion + 1).Set(row => row.UpdatedAt, now);
        if (verdict == "Pending") return await CommitAsync([], null, stageMutation.Build(), ct);
        var version = operation.Version;
        var operationMutation = SurrealWriter.UpdateOnly<WttOperation>(operationId)
            .Where(row => row.Version == version && row.State == "Active" && row.CurrentStageId == stageId)
            .Set(row => row.Version, version + 1).Set(row => row.UpdatedAt, now).Set(row => row.NextAttemptAt, now);
        if (verdict == "ExpiredNoEffect")
        {
            // Proven absence past the exact message's validity: the stage resolves without effect and a new generation may follow.
            var expired = operationMutation.Set(row => row.State, operation.ResumeState ?? "Ready")
                .Set(row => row.Detail, "The disclosed participant message expired unsigned; continue to issue a new one.");
            return await CommitAsync([], (operation.ResumeState is not null ? expired.Unset(row => row.ResumeState) : expired).Build(),
                stageMutation.Set(row => row.State, "ExpiredNoEffect").Build(), ct);
        }
        // The participant broadcast its own exact signature: that signature is the authority; observation now decides the outcome.
        var landed = stageMutation.Set(row => row.State, "SubmissionAuthorized").Set(row => row.AuthorizedAt, now);
        if (stage.PreparedJson is null)
            landed = landed.Set(row => row.PreparedJson, participantPreparedJson!)
                .Set(row => row.PreparedHash, CanonicalJson.HashCanonical(participantPreparedJson!));
        return await CommitAsync([], operationMutation.Set(row => row.Detail, "The participant submitted the disclosed message.").Build(),
            landed.Build(), ct);
    }
}
