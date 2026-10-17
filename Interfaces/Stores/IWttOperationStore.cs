using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>WTT operation journal: offer consumption, stage epochs, first-send authority and evidence projection.</summary>
public interface IWttOperationStore
{
    Task<WttOffer?> GetOfferAsync(string id, CancellationToken ct = default);

    /// <summary>Persists one immutable server offer; an existing identity is never replaced.</summary>
    Task<WttOffer?> CreateOfferAsync(WttOffer offer, CancellationToken ct = default);

    /// <summary>Reads private operation state; transport callers enforce actor/tenant scope.</summary>
    Task<WttOperation?> GetAsync(string id, CancellationToken ct = default);

    Task<WttOperationStage?> GetStageAsync(string id, CancellationToken ct = default);

    /// <summary>Reads at most 256 stages of one operation in creation order.</summary>
    Task<IReadOnlyList<WttOperationStage>> ReadStagesAsync(string operationId, CancellationToken ct = default);

    /// <summary>Lists the participant's operations newest first, strictly before the optional creation cursor.</summary>
    Task<IReadOnlyList<WttOperation>> ListAsync(string actorUserId, string tenantId, DateTimeOffset? before, int limit,
        CancellationToken ct = default);

    /// <summary>Replays the original request first; otherwise consumes its unexpired offer under current source authority.</summary>
    Task<WttOperation?> AdmitAsync(WttOperation candidate, WttStageAuthority authority, CancellationToken ct = default);

    /// <summary>Creates a no-admission tombstone, or cancels an operation with no unresolved stage before any source publication.
    /// A durable-nonce source with a claimed stage cancels only at <paramref name="probedVersion"/>, the version its
    /// reservation probe observed.</summary>
    Task<WttOperation?> CancelAsync(WttOperation candidate, WttStageAuthority authority, long? probedVersion = null,
        CancellationToken ct = default);

    /// <summary>Claims the next stage generation under current authority while every earlier stage is resolved.</summary>
    Task<WttOperationStage?> ClaimStageAsync(string operationId, long expectedVersion, WttOperationStage stage,
        WttStageAuthority authority, CancellationToken ct = default);

    /// <summary>Moves a Planned or Preparing custodial stage to Preparing at a new version before any durable-nonce
    /// reservation, so reservation and cancellation serialize on the stage epoch.</summary>
    Task<WttOperationStage?> BeginPreparationAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, CancellationToken ct = default);

    /// <summary>Fills one validated signed candidate without granting egress.</summary>
    Task<WttOperationStage?> SavePreparedAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, string preparedJson, CancellationToken ct = default);

    /// <summary>Records potential external submission before an unsigned participant message is disclosed.</summary>
    Task<WttOperationStage?> ExposeAsync(string operationId, long expectedVersion, string stageId, long expectedStageVersion,
        string participantJson, WttStageAuthority authority, CancellationToken ct = default);

    /// <summary>Fills an exposed stage with its exact participant-signed candidate without granting server submission.</summary>
    Task<WttOperationStage?> SaveParticipantSignatureAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, string preparedJson, CancellationToken ct = default);

    /// <summary>Atomically authorizes first send of the saved candidate under current authority.</summary>
    Task<WttOperationStage?> AuthorizeAsync(string operationId, long expectedVersion, string stageId, long expectedStageVersion,
        WttStageAuthority authority, CancellationToken ct = default);

    /// <summary>Records provider evidence for an authorized stage; terminal evidence is immutable. A source completion
    /// records the attributed publication and moves to proof in the same transaction.</summary>
    Task<WttOperationStage?> RecordObservationAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, string observationJson, string verdict, WttSourceCompletion? completion = null,
        CancellationToken ct = default);

    /// <summary>Retires a never-signed, never-exposed, never-authorized Planned or Preparing stage whose preparation cannot
    /// complete; callers retire a Preparing durable-nonce stage only after its reservation probe reads none.</summary>
    Task<WttOperationStage?> RetireStageAsync(string operationId, long expectedVersion, string stageId,
        long expectedStageVersion, string detail, CancellationToken ct = default);

    /// <summary>Applies an operation-only transition; evidence fields are set once, except that an unproven envelope is
    /// replaced by the verified one in the same patch that first records its proof.</summary>
    Task<WttOperation?> UpdateAsync(string operationId, long expectedVersion, WttOperationPatch patch,
        CancellationToken ct = default);

    /// <summary>Replaces a recorded proof with a freshly verified one for the same canonical body while no target stage
    /// was ever sent or disclosed; clears the admitted target quote bound to the old signatures.</summary>
    Task<WttOperation?> ReplaceProofAsync(string operationId, long expectedVersion, string expectedProofsJson,
        string transferEnvelopeBase64, string? attestationEnvelopeBase64, string proofsJson, CancellationToken ct = default);

    /// <summary>Reads a bounded due batch for observation, proof and settlement work that needs no new signature.</summary>
    Task<IReadOnlyList<WttOperation>> ReadDueAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>Leases one due operation by advancing its retry time; operation version is unchanged.</summary>
    Task<bool> ClaimRecoveryAsync(string operationId, TimeSpan lease, CancellationToken ct = default);
}
