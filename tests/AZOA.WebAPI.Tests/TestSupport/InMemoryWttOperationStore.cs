using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>Journal double with the Surreal store's epoch, set-once and authority-shape rules; SQL fences are covered by integration tests.</summary>
internal sealed class InMemoryWttOperationStore(TimeProvider? clock = null) : IWttOperationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WttOffer> _offers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WttOperation> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WttOperationStage> _stages = new(StringComparer.Ordinal);
    internal bool LoseNextAdmissionResponse { get; set; }
    internal int Admissions { get; private set; }
    internal bool FailNextPreparationEpoch { get; set; }

    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static bool Terminal(string state) => WttStageStates.IsTerminal(state);
    private static WttOperationIntent Intent(WttOperation operation) => JsonSerializer.Deserialize<WttOperationIntent>(operation.IntentJson)!;

    public Task<WttOffer?> GetOfferAsync(string id, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_offers.TryGetValue(id, out var row) ? Copy(row) : null); }

    public Task<WttOffer?> CreateOfferAsync(WttOffer offer, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_offers.TryAdd(offer.Id, Copy(offer)) ? Copy(offer) : null); }

    public Task<WttOperation?> GetAsync(string id, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_operations.TryGetValue(id, out var row) ? Copy(row) : null); }

    public Task<WttOperationStage?> GetStageAsync(string id, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_stages.TryGetValue(id, out var row) ? Copy(row) : null); }

    public Task<IReadOnlyList<WttOperationStage>> ReadStagesAsync(string operationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var chain = new List<WttOperationStage>();
            var cursor = _operations.TryGetValue(operationId, out var operation) ? operation.CurrentStageId : null;
            while (cursor is not null) { var stage = _stages[cursor]; chain.Add(Copy(stage)); cursor = stage.PreviousStageId; }
            chain.Reverse();
            return Task.FromResult<IReadOnlyList<WttOperationStage>>(chain);
        }
    }

    public Task<IReadOnlyList<WttOperation>> ListAsync(string actorUserId, string tenantId, DateTimeOffset? before, int limit,
        CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<WttOperation>>(_operations.Values
                .Where(row => row.ActorUserId == actorUserId && row.TenantId == tenantId && (before is null || row.CreatedAt < before))
                .OrderByDescending(row => row.CreatedAt).Take(limit).Select(Copy).ToArray());
    }

    public Task<WttOperation?> AdmitAsync(WttOperation candidate, WttStageAuthority authority, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var intent = Intent(candidate);
            if (authority.Signing?.Credential.ActorUserId != intent.ActorUserId) return Task.FromResult<WttOperation?>(null);
            if (_operations.TryGetValue(candidate.Id, out var existing))
                return Task.FromResult(existing.OfferId == candidate.OfferId && existing.IntentJson == candidate.IntentJson ? Copy(existing) : null);
            if (WttSigningAuthorityFence.Stage(intent, "Source", null, authority, "wtt_admission") is null
                || !_offers.TryGetValue(candidate.OfferId, out var offer) || offer.ConsumedByOperationId is not null
                || offer.ExpiresAt <= Now || offer.OfferHash != candidate.OfferHash || offer.OperationId != candidate.Id)
                return Task.FromResult<WttOperation?>(null);
            offer.ConsumedByOperationId = candidate.Id;
            _operations[candidate.Id] = Copy(candidate);
            Admissions++;
            if (LoseNextAdmissionResponse) { LoseNextAdmissionResponse = false; throw new IOException("Admission response lost after commit."); }
            return Task.FromResult<WttOperation?>(Copy(candidate));
        }
    }

    public Task<WttOperation?> CancelAsync(WttOperation candidate, WttStageAuthority authority, long? probedVersion = null,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var intent = Intent(candidate);
            if (WttSigningAuthorityFence.Stage(intent, "Cancel", null, authority, "wtt_fence_cancel") is null) return Task.FromResult<WttOperation?>(null);
            if (!_operations.TryGetValue(candidate.Id, out var existing))
            {
                var tombstone = Copy(candidate);
                tombstone.State = "Cancelled";
                _operations[candidate.Id] = tombstone;
                return Task.FromResult<WttOperation?>(Copy(tombstone));
            }
            if (existing.State == "Cancelled") return Task.FromResult<WttOperation?>(Copy(existing));
            if (existing.Phase != "Source" || existing.TransferPublicationJson is not null || existing.State is not ("Admitted" or "Ready" or "Active")
                || probedVersion is { } probed && probed != existing.Version
                || intent.Source.ChainType == "Ethereum" && existing.CurrentStageId is not null && probedVersion is null)
                return Task.FromResult<WttOperation?>(null);
            if (_stages.Values.Any(stage => stage.OperationId == candidate.Id && stage.Phase == "Source" && stage.Stage == "Transfer"
                    && (stage.AuthorizedAt is not null || stage.ExposedAt is not null) && stage.State is not ("FailedOnChain" or "ExpiredNoEffect")))
                return Task.FromResult<WttOperation?>(null);
            if (existing.CurrentStageId is { } stageId && !Terminal(_stages[stageId].State))
            {
                var stage = _stages[stageId];
                if (stage.Phase != "Source" || stage.AuthorizedAt is not null || stage.ExposedAt is not null || stage.State is not ("Planned" or "Prepared")
                    || intent.Source.ChainType == "Ethereum" && stage.State == "Prepared")
                    return Task.FromResult<WttOperation?>(null);
                stage.State = "Cancelled"; stage.Version++;
            }
            existing.State = "Cancelled"; existing.Version++;
            return Task.FromResult<WttOperation?>(Copy(existing));
        }
    }

    public Task<WttOperationStage?> ClaimStageAsync(string operationId, long expectedVersion, WttOperationStage stage,
        WttStageAuthority authority, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_stages.TryGetValue(stage.Id, out var existing))
                return Task.FromResult(existing.QuoteJson == stage.QuoteJson && existing.ConsentJson == stage.ConsentJson ? Copy(existing) : null);
            var operation = _operations[operationId];
            var resumable = stage.Phase == "Cleanup" && operation.State is "Ready" or "AwaitingProof" or "Settled";
            if (operation.Version != expectedVersion || !resumable && operation.State is not ("Admitted" or "Ready")
                || operation.CurrentStageId != stage.PreviousStageId || stage.Id != WttOperationIdentity.Stage(operationId, stage.Phase, stage.Stage, stage.Generation)
                || stage.PreviousStageId is { } previous && !Terminal(_stages[previous].State))
                return Task.FromResult<WttOperationStage?>(null);
            var original = stage.OriginalStageId is { } originalId && _stages.TryGetValue(originalId, out var found) ? found : null;
            if (stage.Phase == "Cleanup" ? original is null || original.OperationId != operationId || original.Phase is not ("Source" or "Target")
                    || original.State is not ("Confirmed" or "FailedOnChain") || original.PreparedJson is null
                : stage.OriginalStageId is not null)
                return Task.FromResult<WttOperationStage?>(null);
            var fencePhase = stage.ParticipantAddress is not null ? "Participant" : stage.Phase;
            if (WttSigningAuthorityFence.Stage(Intent(operation), fencePhase, original?.Phase, authority, "wtt_fence_stage") is null)
                return Task.FromResult<WttOperationStage?>(null);
            if (resumable) operation.ResumeState = operation.State;
            operation.State = "Active"; operation.CurrentStageId = stage.Id; operation.Version++;
            var saved = Copy(stage);
            saved.CreatedAt = saved.UpdatedAt = Now;
            _stages[stage.Id] = saved;
            return Task.FromResult<WttOperationStage?>(Copy(saved));
        }
    }

    public Task<WttOperationStage?> BeginPreparationAsync(string operationId, long expectedVersion, string stageId, long expectedStageVersion,
        CancellationToken ct = default)
    {
        if (FailNextPreparationEpoch) { FailNextPreparationEpoch = false; throw new IOException("Process stopped after the claim, before its reservation epoch."); }
        return Transition(operationId, expectedVersion, stageId, expectedStageVersion, (operation, stage) =>
        {
            if (operation.State != "Active" || stage.State is not ("Planned" or "Preparing") || stage.Phase is not ("Source" or "Target")
                || stage.ParticipantAddress is not null || stage.PreparedJson is not null || stage.ExposedAt is not null || stage.AuthorizedAt is not null)
                return false;
            stage.State = "Preparing"; stage.UpdatedAt = Now;
            return null;
        });
    }

    public Task<WttOperationStage?> SavePreparedAsync(string operationId, long expectedVersion, string stageId, long expectedStageVersion,
        string preparedJson, CancellationToken ct = default)
        => Transition(operationId, expectedVersion, stageId, expectedStageVersion, (operation, stage) =>
        {
            if (stage.PreparedJson is not null) return stage.PreparedJson == preparedJson;
            if (stage.State is not ("Planned" or "Preparing") || stage.ExposedAt is not null) return false;
            stage.PreparedJson = preparedJson; stage.PreparedHash = CanonicalJson.HashCanonical(preparedJson); stage.State = "Prepared";
            return null;
        });

    public Task<WttOperationStage?> ExposeAsync(string operationId, long expectedVersion, string stageId, long expectedStageVersion,
        string participantJson, WttStageAuthority authority, CancellationToken ct = default)
        => Transition(operationId, expectedVersion, stageId, expectedStageVersion, (operation, stage) =>
        {
            if (stage.ExposedAt is not null) return stage.ParticipantJson == participantJson;
            if (stage.State != "Planned" || stage.ParticipantAddress is null
                || WttSigningAuthorityFence.Stage(Intent(operation), "Participant", null, authority, "wtt_fence_exposure") is null) return false;
            stage.ParticipantJson = participantJson; stage.ExposedAt = Now; stage.State = "Exposed";
            return null;
        });

    public Task<WttOperationStage?> SaveParticipantSignatureAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, string preparedJson, CancellationToken ct = default)
        => Transition(operationId, expectedVersion, stageId, expectedStageVersion, (operation, stage) =>
        {
            if (stage.PreparedJson is not null) return stage.PreparedJson == preparedJson;
            if (stage.State != "Exposed") return false;
            stage.PreparedJson = preparedJson; stage.PreparedHash = CanonicalJson.HashCanonical(preparedJson);
            return null;
        });

    public Task<WttOperationStage?> AuthorizeAsync(string operationId, long expectedVersion, string stageId, long expectedStageVersion,
        WttStageAuthority authority, CancellationToken ct = default)
        => Transition(operationId, expectedVersion, stageId, expectedStageVersion, (operation, stage) =>
        {
            if (stage.AuthorizedAt is not null) return true;
            var original = stage.OriginalStageId is { } id ? _stages[id] : null;
            var phase = stage.ExposedAt is not null || stage.ParticipantAddress is not null ? "Participant" : stage.Phase;
            if (stage.PreparedJson is null || stage.State is not ("Prepared" or "Exposed")
                || WttSigningAuthorityFence.Stage(Intent(operation), phase, original?.Phase, authority, "wtt_fence_send") is null) return false;
            stage.State = "SubmissionAuthorized"; stage.AuthorizedAt = Now;
            return null;
        });

    public Task<WttOperationStage?> RecordObservationAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, string observationJson, string verdict, WttSourceCompletion? completion = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var operation = _operations[operationId];
            var stage = _stages[stageId];
            if (operation.CurrentStageId != stageId || stage.AuthorizedAt is null
                || completion is not null && (verdict != "Confirmed" || stage.Stage != "Transfer" || operation.TransferPublicationJson is not null))
                return Task.FromResult<WttOperationStage?>(null);
            if (Terminal(stage.State)) return Task.FromResult(stage.ObservationJson == observationJson ? Copy(stage) : null);
            if (operation.Version != expectedVersion || stage.Version != expectedStageVersion) return Task.FromResult<WttOperationStage?>(null);
            var next = verdict is "Confirmed" or "FailedOnChain" or "ExpiredNoEffect" or "NeedsOperator" ? verdict : stage.State;
            if (next == stage.State && stage.ObservationJson == observationJson) return Task.FromResult<WttOperationStage?>(Copy(stage));
            stage.ObservationJson = observationJson; stage.Version++;
            if (next != stage.State)
            {
                stage.State = next;
                operation.State = completion is not null ? "AwaitingProof" : next == "NeedsOperator" ? "NeedsOperator" : operation.ResumeState ?? "Ready";
                if (next != "NeedsOperator") operation.ResumeState = null;
                operation.TransferPublicationJson ??= completion?.TransferPublicationJson;
                operation.AttestationPublicationJson ??= completion?.AttestationPublicationJson;
                operation.Version++;
            }
            return Task.FromResult<WttOperationStage?>(Copy(stage));
        }
    }

    public Task<WttOperationStage?> RetireStageAsync(string operationId, long expectedVersion, string stageId, long expectedStageVersion,
        string detail, CancellationToken ct = default)
        => Transition(operationId, expectedVersion, stageId, expectedStageVersion, (operation, stage) =>
        {
            if (stage.State is not ("Planned" or "Preparing") || stage.PreparedJson is not null || stage.ExposedAt is not null
                || stage.AuthorizedAt is not null) return false;
            stage.State = "Retired";
            operation.State = operation.ResumeState ?? "Ready"; operation.ResumeState = null; operation.Detail = detail;
            return null;
        });

    public Task<WttOperation?> UpdateAsync(string operationId, long expectedVersion, WttOperationPatch patch, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var operation = _operations[operationId];
            var firstProof = operation.ProofsJson is null && patch.ProofsJson is not null;
            if (operation.Version != expectedVersion || operation.State is "Settled" or "Cancelled"
                || operation.State == "Active" && patch.State is not (null or "NeedsOperator")
                || !Once(operation.TransferPublicationJson, patch.TransferPublicationJson) || !Once(operation.ProofsJson, patch.ProofsJson)
                || !firstProof && !Once(operation.TransferEnvelopeBase64, patch.TransferEnvelopeBase64) || !Once(operation.BodyClaimId, patch.BodyClaimId)
                || !Once(operation.SettlementJson, patch.SettlementJson) || !Once(operation.TargetQuoteJson, patch.TargetQuoteJson))
                return Task.FromResult<WttOperation?>(null);
            operation.State = patch.State ?? operation.State; operation.Phase = patch.Phase ?? operation.Phase;
            operation.Detail = patch.Detail ?? operation.Detail;
            operation.TransferPublicationJson ??= patch.TransferPublicationJson;
            operation.AttestationPublicationJson ??= patch.AttestationPublicationJson;
            operation.TransferEnvelopeBase64 = firstProof ? patch.TransferEnvelopeBase64 ?? operation.TransferEnvelopeBase64
                : operation.TransferEnvelopeBase64 ?? patch.TransferEnvelopeBase64;
            operation.AttestationEnvelopeBase64 = firstProof ? patch.AttestationEnvelopeBase64 ?? operation.AttestationEnvelopeBase64
                : operation.AttestationEnvelopeBase64 ?? patch.AttestationEnvelopeBase64;
            operation.ProofsJson ??= patch.ProofsJson; operation.BodyClaimId ??= patch.BodyClaimId;
            operation.TargetQuoteJson ??= patch.TargetQuoteJson; operation.SettlementJson ??= patch.SettlementJson;
            operation.NextAttemptAt = patch.NextAttemptAt ?? operation.NextAttemptAt;
            operation.Version++;
            return Task.FromResult<WttOperation?>(Copy(operation));
        }
    }

    public Task<WttOperation?> ReplaceProofAsync(string operationId, long expectedVersion, string expectedProofsJson,
        string transferEnvelopeBase64, string? attestationEnvelopeBase64, string proofsJson, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var operation = _operations[operationId];
            if (operation.Version != expectedVersion || operation.Phase != "Target" || operation.State is not ("Ready" or "NeedsOperator")
                || operation.ProofsJson != expectedProofsJson
                || (operation.AttestationEnvelopeBase64 is null) != (attestationEnvelopeBase64 is null)
                || _stages.Values.Any(stage => stage.OperationId == operationId && stage.Phase == "Target"
                    && (stage.AuthorizedAt is not null || stage.ExposedAt is not null)))
                return Task.FromResult<WttOperation?>(null);
            operation.TransferEnvelopeBase64 = transferEnvelopeBase64;
            operation.AttestationEnvelopeBase64 = attestationEnvelopeBase64 ?? operation.AttestationEnvelopeBase64;
            operation.ProofsJson = proofsJson; operation.TargetQuoteJson = null; operation.State = "Ready"; operation.Version++;
            return Task.FromResult<WttOperation?>(Copy(operation));
        }
    }

    public Task<IReadOnlyList<WttOperation>> ReadDueAsync(int limit = 50, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<WttOperation>>(_operations.Values
                .Where(row => row.NextAttemptAt <= Now && row.State is "Active" or "AwaitingProof" or "Ready" or "NeedsOperator")
                .Take(limit).Select(Copy).ToArray());
    }

    public Task<bool> ClaimRecoveryAsync(string operationId, TimeSpan lease, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var operation = _operations[operationId];
            if (operation.NextAttemptAt > Now) return Task.FromResult(false);
            operation.NextAttemptAt = Now.Add(lease);
            return Task.FromResult(true);
        }
    }

    private Task<WttOperationStage?> Transition(string operationId, long expectedVersion, string stageId, long expectedStageVersion,
        Func<WttOperation, WttOperationStage, bool?> mutate)
    {
        lock (_gate)
        {
            var operation = _operations[operationId];
            var stage = _stages[stageId];
            if (operation.CurrentStageId != stageId) return Task.FromResult<WttOperationStage?>(null);
            var before = (Copy(operation), Copy(stage));
            if (operation.Version != expectedVersion || stage.Version != expectedStageVersion)
            {
                var replay = mutate(Copy(operation), Copy(stage));
                return Task.FromResult(replay == true ? Copy(stage) : null);
            }
            var result = mutate(operation, stage);
            if (result is false) { _operations[operationId] = before.Item1; _stages[stageId] = before.Item2; return Task.FromResult<WttOperationStage?>(null); }
            if (result is true) return Task.FromResult<WttOperationStage?>(Copy(stage));
            operation.Version++; stage.Version++;
            return Task.FromResult<WttOperationStage?>(Copy(stage));
        }
    }

    private static bool Once(string? current, string? proposed) => proposed is null || current is null || current == proposed;
}

/// <summary>Body-claim double with the Surreal store's immutable-owner replay rule.</summary>
internal sealed class InMemoryWttBodyClaimStore : IWttBodyClaimStore
{
    private readonly Dictionary<string, WttBodyClaim> _claims = new(StringComparer.Ordinal);

    public Task<WttBodyClaim?> GetAsync(string id, CancellationToken ct = default)
    { lock (_claims) return Task.FromResult(_claims.TryGetValue(id, out var claim) ? claim : null); }

    public Task<WttBodyClaim?> ClaimAsync(WttBodyClaim candidate, CancellationToken ct = default)
    {
        lock (_claims)
        {
            var publication = _claims.Values.FirstOrDefault(row => row.TargetPartition == candidate.TargetPartition
                && row.EmitterChain == candidate.EmitterChain && row.EmitterAddress == candidate.EmitterAddress && row.Sequence == candidate.Sequence);
            if (_claims.TryGetValue(candidate.Id, out var existing) || publication is not null)
                return Task.FromResult(existing is not null && WttBodyClaimIdentity.SameOwner(existing, candidate) ? existing : null);
            _claims[candidate.Id] = candidate;
            return Task.FromResult<WttBodyClaim?>(candidate);
        }
    }
}
