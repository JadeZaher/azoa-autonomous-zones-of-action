using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Persists immutable mandate offers, accepted authority and exact audited recovery.</summary>
public interface IHoldReleaseMandateStore
{
    /// <summary>Reads the original mandate without authorizing any effect.</summary>
    Task<HoldReleaseMandate?> GetAsync(string id, CancellationToken ct = default);
    /// <summary>Reads the immutable disclosure selected before acceptance.</summary>
    Task<HoldReleaseMandateOffer?> GetOfferAsync(string id, CancellationToken ct = default);
    /// <summary>Persists a server-produced exact disclosure under current owner authority.</summary>
    Task<HoldReleaseMandateOffer?> OfferAsync(HoldReleaseMandateRequest request, SigningConsentWitness authority, CancellationToken ct = default);
    /// <summary>Atomically accepts an exact disclosure and reserves the original custody liability.</summary>
    Task<HoldReleaseMandate?> AdmitAsync(HoldReleaseMandateAcceptance acceptance, SigningConsentWitness authority, CancellationToken ct = default);
    /// <summary>Appends current authority to unchanged active terms without reviving a terminal mandate.</summary>
    Task<HoldReleaseMandate?> ReauthorizeAsync(string id, HoldRecoveryRequest request, SigningConsentWitness authority, CancellationToken ct = default);
    /// <summary>Withdraws future mandate authority while preserving already authorized settlement.</summary>
    Task<HoldReleaseMandate?> RevokeAsync(string id, HoldRecoveryRequest request, SigningCredentialWitness credential, CancellationToken ct = default);
    /// <summary>Lists bounded due active mandates without implying current authority.</summary>
    Task<IReadOnlyList<HoldReleaseMandate>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default);
    /// <summary>Claims one observation schedule epoch without changing immutable authority.</summary>
    Task<bool> ScheduleAsync(HoldReleaseMandate expected, DateTimeOffset next, CancellationToken ct = default);
    /// <summary>Lists only the exact owner's original tenant scope.</summary>
    Task<IReadOnlyList<HoldReleaseMandate>> ListOwnerAsync(Guid actor, Guid? tenant, int offset, int limit, CancellationToken ct = default);
    /// <summary>Recovers an exact original management receipt without authorizing another mutation.</summary>
    Task<HoldReleaseMandate?> GetRecoveryAsync(string id, string kind, HoldRecoveryRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default);
}
