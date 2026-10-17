using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Authenticated NFT lifecycle backed by durable prepared transactions and chain evidence.</summary>
public interface INftLifecycleService
{
    /// <summary>Quotes the exact proven-no-effect replacement generation without signing.</summary>
    Task<AZOAResult<NftProtocolCostQuote>> QuoteReplacementCostsAsync(string operationId, NftStageAcceptance stage,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness credential, CancellationToken ct = default);
    /// <summary>Atomically records fresh replacement costs without changing the original command.</summary>
    Task<AZOAResult<NftLifecycleView>> AcceptReplacementCostsAsync(string operationId, NftReplacementCostAcceptance request,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness credential, CancellationToken ct = default);
    /// <summary>Reads one exact cost acceptance outcome without creating signing authority.</summary>
    Task<AZOAResult<NftReplacementCostReconciliation>> ReconcileReplacementCostsAsync(string operationId, NftReplacementCostAcceptance request,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness credential, CancellationToken ct = default);
    /// <summary>Fences an unanswered exact acceptance version before its offer can be discarded.</summary>
    Task<AZOAResult<NftReplacementCostReconciliation>> RetireReplacementCostsAsync(string operationId, NftReplacementCostAcceptance request,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness credential, CancellationToken ct = default);
    /// <summary>Admits the exact explicitly accepted workflow mint and audit atomically without signing.</summary>
    Task<AZOAResult<NftLifecycleView>> AdmitWorkflowMintAsync(UserNFTMintModel request,
        NftWorkflowCostAcceptanceReceipt receipt, CancellationToken ct = default);
    /// <summary>Issues an immutable cost receipt for the exact mint draft without admission or custody.</summary>
    Task<AZOAResult<NftProtocolCostQuote>> QuoteMintCostsAsync(Guid actor, Guid recipient, UserNFTMintModel request,
        string requestKey, CancellationToken ct = default, Guid actingTenantId = default);

    /// <summary>Admits immutable issuance intent without preparing, signing, or submitting transactions.</summary>
    Task<AZOAResult<NftLifecycleView>> AdmitMintAsync(Guid actor, Guid recipient, UserNFTMintModel request, string requestKey, CancellationToken ct = default, Guid actingTenantId = default, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null);
    /// <summary>Admits a transfer or burn without preparing, signing, or submitting transactions.</summary>
    Task<AZOAResult<NftLifecycleView>> AdmitMoveAsync(Guid actor, Guid nftId, string? recipientAddress, bool burn, string requestKey, CancellationToken ct = default, Guid actingTenantId = default, NftFeeAuthorization? feeAuthorization = null, Guid? sourceWalletId = null, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null);
    /// <summary>Admits proven issuer revocation without preparing, signing, or submitting transactions.</summary>
    Task<AZOAResult<NftLifecycleView>> AdmitRevokeAsync(Guid actor, Guid nftId, string requestKey, CancellationToken ct = default, Guid actingTenantId = default, NftFeeAuthorization? feeAuthorization = null, string? reason = null, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null);
    /// <summary>Admits a new NFT or credential issuance with immutable authenticated parties and fee consent.</summary>
    Task<AZOAResult<NftLifecycleView>> MintAsync(Guid actor, Guid recipient, UserNFTMintModel request, string requestKey, CancellationToken ct = default, Guid actingTenantId = default, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null, AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null);
    /// <summary>Transfers or destroys a proven current asset through its durable chain lifecycle.</summary>
    Task<AZOAResult<NftLifecycleView>> MoveAsync(Guid actor, Guid nftId, string? recipientAddress, bool burn, string requestKey, CancellationToken ct = default, Guid actingTenantId = default, NftFeeAuthorization? feeAuthorization = null, Guid? sourceWalletId = null, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null, AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null);
    /// <summary>Reconciles an admitted operation or accepts a participant-specific signing stage.</summary>
    Task<AZOAResult<NftLifecycleView>> RefreshAsync(Guid actor, string operationId, bool accept, CancellationToken ct = default, Guid actingTenantId = default,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null, NftStageAcceptance? acceptance = null);
    /// <summary>Proves current application and chain ownership for authorization.</summary>
    Task<bool> IsCurrentOwnerAsync(Guid actor, Guid nftId, CancellationToken ct = default);
    /// <summary>Cancels an unsent request owned by the caller; dispatched transactions must be reconciled.</summary>
    Task<AZOAResult<NftLifecycleView>> CancelAsync(Guid actor, string operationId, CancellationToken ct = default);
    /// <summary>Starts an issuer-authorized clawback using confirmed issuance provenance.</summary>
    Task<AZOAResult<NftLifecycleView>> RevokeAsync(Guid actor, Guid nftId, string requestKey, CancellationToken ct = default, Guid actingTenantId = default, NftFeeAuthorization? feeAuthorization = null, string? reason = null, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null, AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null);
    /// <summary>Reads the public lifecycle state for either participant without signing or submitting.</summary>
    Task<AZOAResult<NftLifecycleView>> GetAsync(Guid actor, string operationId, CancellationToken ct = default);
    /// <summary>Returns the explicit platform-fee quote before any signing or admission.</summary>
    Task<AZOAResult<NftFeeQuote>> QuoteFeeAsync(Guid actor, string chainType, string operation, CancellationToken ct = default, Guid? nftId = null, string? sourceOperationId = null);
    /// <summary>Discovers the caller's pending lifecycles with an opaque snapshot cursor.</summary>
    Task<AZOAResult<NftLifecyclePage>> ListPendingAsync(Guid actor, string? cursor, int limit = 25, CancellationToken ct = default);
    /// <summary>Resolves an authenticated request key; absence does not prove a delayed admission cannot arrive.</summary>
    Task<AZOAResult<NftLifecycleView>> GetRequestAsync(Guid actor, string operation, string requestKey, Guid actingTenantId = default, CancellationToken ct = default);
    /// <summary>Atomically fences a never-admitted key or cancels its existing unsent lifecycle.</summary>
    Task<AZOAResult<NftLifecycleView>> CancelRequestAsync(Guid actor, string operation, string requestKey, Guid actingTenantId = default, CancellationToken ct = default);
    /// <summary>Requests return of the exact original confirmed transfer; its current holder must authorize.</summary>
    Task<AZOAResult<NftLifecycleView>> RefundAsync(Guid actor, string sourceLifecycleId, string requestKey,
        CancellationToken ct = default, Guid actingTenantId = default, NftFeeAuthorization? feeAuthorization = null,
        AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null, AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null);
    /// <summary>Admits a refund request without participant signing or transaction preparation.</summary>
    Task<AZOAResult<NftLifecycleView>> AdmitRefundAsync(Guid actor, string sourceLifecycleId, string requestKey,
        CancellationToken ct = default, Guid actingTenantId = default, NftFeeAuthorization? feeAuthorization = null,
        AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null);
}
