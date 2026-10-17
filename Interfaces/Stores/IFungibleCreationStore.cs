using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Persists creation admission, exact-wire transitions and atomic asset projection.</summary>
public interface IFungibleCreationStore
{
    /// <summary>Replays the original request or admits an unexpired quote under current action authority.</summary>
    Task<FungibleCreationOperation?> ClaimAsync(FungibleCreationOperation candidate, CancellationToken ct = default);
    Task<FungibleCreationOperation?> GetAsync(string id, CancellationToken ct = default);
    /// <summary>Cancels only an exact unsent or authoritatively absent generation and preserves all wire history.</summary>
    Task<FungibleCreationOperation?> CancelAsync(string id, AZOA.WebAPI.Models.Blockchain.FungibleContinuationRequest request,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness credential, CancellationToken ct = default);
    /// <summary>Archives exact no-effect proof and opens one freshly authorized generation awaiting a new quote.</summary>
    Task<FungibleCreationOperation?> ReplaceAsync(FungibleCreationOperation expected, FungibleCreationAuthorization authorization,
        CancellationToken ct = default);
    /// <summary>Records fresh authority for an unchanged operation while preserving original command and signed bytes.</summary>
    Task<FungibleCreationOperation?> AuthorizeAsync(FungibleCreationOperation expected, FungibleCreationAuthorization authorization,
        CancellationToken ct = default);
    /// <summary>Discloses an exact finalized attachment only to its requested Holon's current owner.</summary>
    Task<AZOA.WebAPI.Models.Blockchain.FungibleAttachmentView?> GetAttachmentAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default);
    /// <summary>Replays only an exact immutable attachment receipt without requiring permission to repeat its effect.</summary>
    Task<AZOA.WebAPI.Models.Blockchain.FungibleAttachmentView?> ReplayAttachmentAsync(string id,
        AZOA.WebAPI.Models.Blockchain.FungibleAttachmentRequest request, Guid actor, Guid? tenant, CancellationToken ct = default);
    /// <summary>Commits current owner consent, original finalized association and completion together.</summary>
    Task<AZOA.WebAPI.Models.Blockchain.FungibleAttachmentView?> AttachAsync(string id,
        AZOA.WebAPI.Models.Blockchain.FungibleAttachmentRequest request, AZOA.WebAPI.Models.Signing.SigningConsentWitness consent,
        CancellationToken ct = default);
    /// <summary>Offers or accepts the exact quote while the original request still awaits participant consent.</summary>
    Task<FungibleCreationOperation?> SetQuoteAsync(FungibleCreationOperation expected, string quoteJson, bool accept,
        long? expectedPolicyVersion, CancellationToken ct = default);
    /// <summary>Updates only the observed immutable operation version; new authorization also fences workflow authority.</summary>
    Task<FungibleCreationOperation?> UpdateAsync(FungibleCreationOperation candidate, long expectedVersion,
        string expectedState, bool authorizeEffect, long? expectedPolicyVersion, CancellationToken ct = default);
    /// <summary>Commits original issuance, optional reserve and holon projection with completion in one transaction.</summary>
    Task<bool> CompleteAsync(FungibleCreationOperation expected, FungibleAssetAuthority authority,
        PlatformReserveLedger? reserve, CancellationToken ct = default);
    Task<IReadOnlyList<FungibleCreationOperation>> ListParticipantAsync(Guid actor, Guid? tenant, int offset, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<FungibleCreationOperation>> ListRecoverableAsync(int limit, CancellationToken ct = default);
    /// <summary>Rotates one observed recovery row without changing its effect authority.</summary>
    Task TouchRecoveryAsync(FungibleCreationOperation expected, CancellationToken ct = default);
}
