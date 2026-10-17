using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealWttOperationStore
{
    /// <inheritdoc/>
    public async Task<WttOperationStage?> ClaimStageAsync(string operationId, long expectedVersion, WttOperationStage stage,
        WttStageAuthority authority, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        if (operation is null || stage is null || stage.OperationId != operationId || stage.IntentHash != operation.IntentHash
            || stage.Id != WttOperationIdentity.Stage(operationId, stage.Phase, stage.Stage, stage.Generation) || stage.Generation < 0
            || string.IsNullOrWhiteSpace(stage.Stage) || stage.Version != 0 || stage.State != "Planned" || stage.PreparedJson is not null
            || stage.ParticipantJson is not null || stage.ObservationJson is not null || stage.ExposedAt is not null
            || stage.AuthorizedAt is not null || !Canonical(stage.QuoteJson, stage.QuoteHash)
            || stage.ConsentJson is not null && !Canonical(stage.ConsentJson, null))
            return null;
        var existing = await GetStageAsync(stage.Id, ct);
        if (existing is not null)
            return existing.OperationId == operationId && existing.QuoteJson == stage.QuoteJson && existing.ConsentJson == stage.ConsentJson
                && existing.PreviousStageId == stage.PreviousStageId ? existing : null;
        var resumable = stage.Phase == "Cleanup" && operation.State is "Ready" or "AwaitingProof" or "Settled";
        if (operation.Version != expectedVersion || !resumable && operation.State is not ("Admitted" or "Ready")
            || operation.CurrentStageId != stage.PreviousStageId) return null;
        if (stage.PreviousStageId is { } previousId)
        {
            var previousStage = await GetStageAsync(previousId, ct);
            if (previousStage is null || previousStage.OperationId != operationId || !WttStageStates.IsTerminal(previousStage.State)) return null;
        }
        string? originalPhase = null;
        if (stage.Phase == "Cleanup")
        {
            var original = stage.OriginalStageId is { } originalId ? await GetStageAsync(originalId, ct) : null;
            if (original is null || original.OperationId != operationId || original.Phase is not ("Source" or "Target")
                || original.State is not ("Confirmed" or "FailedOnChain") || original.PreparedJson is null) return null;
            originalPhase = original.Phase;
        }
        else if (stage.OriginalStageId is not null) return null;
        var guards = WttSigningAuthorityFence.Stage(Intent(operation), FencePhase(stage), originalPhase, authority, "wtt_fence_stage");
        if (guards is null) return null;
        var now = Now;
        var candidate = Clone(stage);
        candidate.CreatedAt = now;
        candidate.UpdatedAt = now;
        var state = operation.State;
        var mutation = stage.PreviousStageId is { } prior
            ? SurrealWriter.UpdateOnly<WttOperation>(operationId)
                .Where(row => row.Version == expectedVersion && row.State == state && row.CurrentStageId == prior)
            : SurrealWriter.UpdateOnly<WttOperation>(operationId)
                .Where(row => row.Version == expectedVersion && row.State == state && row.CurrentStageId == null);
        var claimed = mutation.Set(row => row.State, "Active").Set(row => row.CurrentStageId, stage.Id)
            .Set(row => row.Detail, "Stage " + stage.Stage + " claimed.").Set(row => row.Version, expectedVersion + 1)
            .Set(row => row.UpdatedAt, now);
        var operationMutation = (resumable ? claimed.Set(row => row.ResumeState, state) : claimed).Build();
        var saved = await CommitAsync(guards, operationMutation, SurrealWriter.Create(candidate), ct);
        if (saved is not null) return saved;
        var winner = await GetStageAsync(stage.Id, ct);
        return winner?.OperationId == operationId && winner.QuoteJson == stage.QuoteJson && winner.ConsentJson == stage.ConsentJson ? winner : null;
    }

    /// <inheritdoc/>
    public async Task<WttOperationStage?> BeginPreparationAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, CancellationToken ct = default)
    {
        var (operation, stage) = await CurrentAsync(operationId, stageId, ct);
        if (operation is null || stage is null || operation.Version != expectedVersion || operation.State != "Active"
            || stage.Version != expectedStageVersion || stage.State is not ("Planned" or "Preparing") || stage.Phase is not ("Source" or "Target")
            || stage.ParticipantAddress is not null || stage.PreparedJson is not null || stage.ExposedAt is not null || stage.AuthorizedAt is not null)
            return null;
        var now = Now;
        var stageState = stage.State;
        var operationMutation = SurrealWriter.UpdateOnly<WttOperation>(operationId)
            .Where(row => row.Version == expectedVersion && row.State == "Active" && row.CurrentStageId == stageId)
            .Set(row => row.Detail, "Stage " + stage.Stage + " is reserving its account nonce.")
            .Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<WttOperationStage>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == stageState && row.PreparedJson == null
                && row.ExposedAt == null && row.AuthorizedAt == null)
            .Set(row => row.State, "Preparing").Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitAsync([], operationMutation, stageMutation, ct);
    }

    /// <inheritdoc/>
    public async Task<WttOperationStage?> SavePreparedAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, string preparedJson, CancellationToken ct = default)
    {
        var (operation, stage) = await CurrentAsync(operationId, stageId, ct);
        if (operation is null || stage is null || !Canonical(preparedJson, null)) return null;
        if (stage.PreparedJson is not null) return stage.PreparedJson == preparedJson ? stage : null;
        if (operation.Version != expectedVersion || operation.State != "Active" || stage.Version != expectedStageVersion
            || stage.State is not ("Planned" or "Preparing") || stage.ExposedAt is not null) return null;
        var now = Now;
        var stageState = stage.State;
        var operationMutation = SurrealWriter.UpdateOnly<WttOperation>(operationId)
            .Where(row => row.Version == expectedVersion && row.State == "Active" && row.CurrentStageId == stageId)
            .Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<WttOperationStage>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == stageState && row.PreparedJson == null && row.ExposedAt == null)
            .Set(row => row.PreparedJson, preparedJson).Set(row => row.PreparedHash, CanonicalJson.HashCanonical(preparedJson))
            .Set(row => row.State, "Prepared").Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitAsync([], operationMutation, stageMutation, ct);
    }

    /// <inheritdoc/>
    public async Task<WttOperationStage?> ExposeAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, string participantJson, WttStageAuthority authority, CancellationToken ct = default)
    {
        var (operation, stage) = await CurrentAsync(operationId, stageId, ct);
        if (operation is null || stage is null || !Canonical(participantJson, null) || stage.ParticipantAddress is null) return null;
        if (stage.ExposedAt is not null) return stage.ParticipantJson == participantJson ? stage : null;
        if (operation.Version != expectedVersion || operation.State != "Active" || stage.Version != expectedStageVersion
            || stage.State != "Planned" || stage.PreparedJson is not null || stage.AuthorizedAt is not null) return null;
        var guards = WttSigningAuthorityFence.Stage(Intent(operation), "Participant", null, authority, "wtt_fence_exposure");
        if (guards is null) return null;
        var now = Now;
        var operationMutation = SurrealWriter.UpdateOnly<WttOperation>(operationId)
            .Where(row => row.Version == expectedVersion && row.State == "Active" && row.CurrentStageId == stageId)
            .Set(row => row.Detail, "Awaiting the participant's exact signature.").Set(row => row.Version, expectedVersion + 1)
            .Set(row => row.UpdatedAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<WttOperationStage>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == "Planned" && row.ExposedAt == null
                && row.PreparedJson == null && row.AuthorizedAt == null)
            .Set(row => row.ParticipantJson, participantJson).Set(row => row.ExposedAt, now).Set(row => row.State, "Exposed")
            .Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitAsync(guards, operationMutation, stageMutation, ct);
    }

    /// <inheritdoc/>
    public async Task<WttOperationStage?> SaveParticipantSignatureAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, string preparedJson, CancellationToken ct = default)
    {
        var (operation, stage) = await CurrentAsync(operationId, stageId, ct);
        if (operation is null || stage is null || !Canonical(preparedJson, null) || stage.ExposedAt is null) return null;
        if (stage.PreparedJson is not null) return stage.PreparedJson == preparedJson ? stage : null;
        if (operation.Version != expectedVersion || operation.State != "Active" || stage.Version != expectedStageVersion
            || stage.State != "Exposed") return null;
        var now = Now;
        var operationMutation = SurrealWriter.UpdateOnly<WttOperation>(operationId)
            .Where(row => row.Version == expectedVersion && row.State == "Active" && row.CurrentStageId == stageId)
            .Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<WttOperationStage>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == "Exposed" && row.PreparedJson == null)
            .Set(row => row.PreparedJson, preparedJson).Set(row => row.PreparedHash, CanonicalJson.HashCanonical(preparedJson))
            .Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitAsync([], operationMutation, stageMutation, ct);
    }

    /// <inheritdoc/>
    public async Task<WttOperationStage?> AuthorizeAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, WttStageAuthority authority, CancellationToken ct = default)
    {
        var (operation, stage) = await CurrentAsync(operationId, stageId, ct);
        if (operation is null || stage is null) return null;
        if (stage.AuthorizedAt is not null) return stage;
        if (operation.Version != expectedVersion || operation.State != "Active" || stage.Version != expectedStageVersion
            || stage.PreparedJson is null || stage.PreparedHash != CanonicalJson.HashCanonical(stage.PreparedJson)
            || stage.State is not ("Prepared" or "Exposed")) return null;
        string? originalPhase = null;
        if (stage.Phase == "Cleanup")
        {
            var original = stage.OriginalStageId is { } originalId ? await GetStageAsync(originalId, ct) : null;
            if (original is null || original.OperationId != operationId) return null;
            originalPhase = original.Phase;
        }
        var guards = WttSigningAuthorityFence.Stage(Intent(operation), stage.ExposedAt is null ? FencePhase(stage) : "Participant",
            originalPhase, authority, "wtt_fence_send");
        if (guards is null) return null;
        var now = Now;
        var stageState = stage.State;
        var operationMutation = SurrealWriter.UpdateOnly<WttOperation>(operationId)
            .Where(row => row.Version == expectedVersion && row.State == "Active" && row.CurrentStageId == stageId)
            .Set(row => row.Detail, "First send of the saved " + stage.Stage + " candidate is authorized.")
            .Set(row => row.NextAttemptAt, now).Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<WttOperationStage>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == stageState && row.AuthorizedAt == null && row.PreparedJson != null)
            .Set(row => row.State, "SubmissionAuthorized").Set(row => row.AuthorizedAt, now)
            .Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitAsync(guards, operationMutation, stageMutation, ct);
    }

    /// <inheritdoc/>
    public async Task<WttOperationStage?> RecordObservationAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, string observationJson, string verdict, WttSourceCompletion? completion = null,
        CancellationToken ct = default)
    {
        var (operation, stage) = await CurrentAsync(operationId, stageId, ct);
        if (operation is null || stage is null || !Canonical(observationJson, null) || stage.AuthorizedAt is null) return null;
        if (completion is not null && (verdict != "Confirmed" || stage.Phase != "Source" || stage.Stage != "Transfer"
                || operation.Phase != "Source" || operation.TransferPublicationJson is not null
                || !Canonical(completion.TransferPublicationJson, null)
                || completion.AttestationPublicationJson is { } attestation && !Canonical(attestation, null)))
            return null;
        if (WttStageStates.IsTerminal(stage.State)) return stage.ObservationJson == observationJson ? stage : null;
        if (operation.Version != expectedVersion || operation.State is not ("Active" or "NeedsOperator")
            || stage.Version != expectedStageVersion || stage.State is not ("SubmissionAuthorized" or "NeedsOperator")) return null;
        var now = Now;
        var stageState = stage.State;
        var nextStageState = verdict is "Confirmed" or "FailedOnChain" or "ExpiredNoEffect" or "NeedsOperator" ? verdict : stageState;
        if (nextStageState == stageState && stage.ObservationJson == observationJson) return stage;
        var stageMutation = SurrealWriter.UpdateOnly<WttOperationStage>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == stageState && row.AuthorizedAt != null)
            .Set(row => row.ObservationJson, observationJson).Set(row => row.State, nextStageState)
            .Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        if (nextStageState == stageState)
            return await CommitAsync([], null, stageMutation, ct);
        var operationState = operation.State;
        var terminal = nextStageState != "NeedsOperator";
        var nextOperationState = completion is not null ? "AwaitingProof" : terminal ? operation.ResumeState ?? "Ready" : "NeedsOperator";
        var observed = (completion is null
                ? SurrealWriter.UpdateOnly<WttOperation>(operationId)
                    .Where(row => row.Version == expectedVersion && row.State == operationState && row.CurrentStageId == stageId)
                : SurrealWriter.UpdateOnly<WttOperation>(operationId)
                    .Where(row => row.Version == expectedVersion && row.State == operationState && row.CurrentStageId == stageId
                        && row.TransferPublicationJson == null))
            .Set(row => row.State, nextOperationState).Set(row => row.Detail, "Stage " + stage.Stage + " observed " + nextStageState + ".")
            .Set(row => row.NextAttemptAt, now).Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, now);
        if (completion is not null)
        {
            // Publication evidence and the end of cancellability commit with the confirming observation, leaving no Ready window.
            observed = observed.Set(row => row.TransferPublicationJson, completion.TransferPublicationJson);
            if (completion.AttestationPublicationJson is { } attestationJson) observed = observed.Set(row => row.AttestationPublicationJson, attestationJson);
        }
        var operationMutation = (terminal && operation.ResumeState is not null ? observed.Unset(row => row.ResumeState) : observed).Build();
        return await CommitAsync([], operationMutation, stageMutation, ct);
    }

    /// <inheritdoc/>
    public async Task<WttOperationStage?> RetireStageAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, string detail, CancellationToken ct = default)
    {
        var (operation, stage) = await CurrentAsync(operationId, stageId, ct);
        if (operation is null || stage is null) return null;
        if (stage.State == "Retired") return stage;
        if (operation.Version != expectedVersion || operation.State != "Active" || stage.Version != expectedStageVersion
            || stage.State is not ("Planned" or "Preparing") || stage.PreparedJson is not null || stage.ExposedAt is not null
            || stage.AuthorizedAt is not null) return null;
        var now = Now;
        var stageState = stage.State;
        var reason = string.IsNullOrWhiteSpace(detail) ? "Unsigned stage retired before any signature or disclosure." : detail[..Math.Min(detail.Length, 512)];
        var retired = SurrealWriter.UpdateOnly<WttOperation>(operationId)
            .Where(row => row.Version == expectedVersion && row.State == "Active" && row.CurrentStageId == stageId)
            .Set(row => row.State, operation.ResumeState ?? "Ready").Set(row => row.Detail, reason).Set(row => row.Version, expectedVersion + 1)
            .Set(row => row.UpdatedAt, now);
        var operationMutation = (operation.ResumeState is not null ? retired.Unset(row => row.ResumeState) : retired).Build();
        var stageMutation = SurrealWriter.UpdateOnly<WttOperationStage>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == stageState && row.PreparedJson == null
                && row.ExposedAt == null && row.AuthorizedAt == null)
            .Set(row => row.State, "Retired").Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitAsync([], operationMutation, stageMutation, ct);
    }

    private async Task<(WttOperation? Operation, WttOperationStage? Stage)> CurrentAsync(string operationId, string stageId,
        CancellationToken ct)
    {
        var operation = await GetAsync(operationId, ct);
        var stage = await GetStageAsync(stageId, ct);
        if (operation is null || stage is null || operation.CurrentStageId != stageId || stage.OperationId != operationId
            || stage.IntentHash != operation.IntentHash || stage.QuoteHash != CanonicalJson.HashCanonical(stage.QuoteJson)) return (null, null);
        return (operation, stage);
    }

    private static string FencePhase(WttOperationStage stage)
        => stage.ParticipantAddress is not null ? "Participant" : stage.Phase;

    private async Task<WttOperationStage?> CommitAsync(IReadOnlyList<SurrealQuery> guards, SurrealQuery? operationMutation,
        SurrealQuery stageMutation, CancellationToken ct)
    {
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        // raw: current authority, operation epoch and stage transition commit together or all roll back.
        if (operationMutation is not null)
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(operationMutation, "wtt_gen_operation", "_wtt_operation_saved"));
            statements.Add(SurrealQuery.Of("IF $_wtt_operation_saved = NONE OR $_wtt_operation_saved = [] { THROW 'WTT_JOURNAL_CONFLICT' }"));
        }
        statements.Add(SurrealGeneratedStatementScope.Assign(stageMutation, "wtt_gen_stage", "_wtt_stage_saved"));
        statements.Add(SurrealQuery.Of("IF $_wtt_stage_saved = NONE OR $_wtt_stage_saved = [] { THROW 'WTT_JOURNAL_CONFLICT' }"));
        var stageIndex = statements.Count;
        statements.Add(SurrealQuery.Of("RETURN $_wtt_stage_saved"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            var response = await ExecuteAsync(statements, ct);
            return response.GetValues<WttOperationStage>(stageIndex).Single();
        }
        catch (SurrealStatementException exception) when (ExpectedConflict(exception))
        {
            return null;
        }
    }
}
