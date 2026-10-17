using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Persists immutable hold commands alongside custody liabilities and reserve accounting.</summary>
public interface IHoldCommandStore
{
    /// <summary>Reads the original immutable terminal command receipt.</summary>
    Task<HoldCommandReceipt?> GetReceiptAsync(string operationId, CancellationToken ct = default);

    /// <summary>Reads the exact network, protocol, owner and tenant bound aggregate.</summary>
    Task<HoldLedger?> GetHoldAsync(string holdRecordId, CancellationToken ct = default);

    /// <summary>Resolves an original owner's human reference only when its network-qualified identity is unambiguous.</summary>
    Task<HoldLedger?> FindOwnedHoldAsync(Guid owner, Guid? tenant, string chain, AZOA.WebAPI.Core.ChainNetwork network,
        string holdId, CancellationToken ct = default);

    /// <summary>Reads an admitted command without advancing custody.</summary>
    Task<HoldCommand?> GetCommandAsync(string operationId, CancellationToken ct = default);

    /// <summary>Lists original-actor/tenant commands or a direct recipient's pinned acceptance journeys.</summary>
    Task<IReadOnlyList<HoldCommand>> ListParticipantAsync(Guid actor, Guid? tenant, int offset, int limit, CancellationToken ct = default);

    /// <summary>Lists a bounded due page containing only already-authorized egress.</summary>
    Task<IReadOnlyList<HoldCommand>> ListReconciliationAsync(DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>Advances scheduling for the observed version without granting preparation authority.</summary>
    Task<bool> ScheduleReconciliationAsync(HoldCommand expected, DateTimeOffset next, CancellationToken ct = default);

    /// <summary>Reads an immutable recovery receipt for exact participant request replay.</summary>
    Task<HoldRecoveryReceipt?> GetRecoveryAsync(string receiptId, CancellationToken ct = default);

    /// <summary>Reads exact original transfer or recipient consent without advancing custody.</summary>
    Task<HoldCommandAuthorization?> GetAuthorizationAsync(string receiptId, CancellationToken ct = default);

    /// <summary>Reserves one exact funded or outbound liability and immutable quote without authorizing egress.</summary>
    Task<HoldCommand?> ClaimRailAsync(HoldCommandIntent intent, HoldRailPlan plan, HoldCommandAuthority authority,
        CancellationToken ct = default);

    /// <summary>Records explicit current consent to the exact quote, preserved generation and signed bytes.</summary>
    Task<HoldCommand?> AuthorizeRailAsync(HoldCommand expected, HoldCommandAuthorization authorization,
        CancellationToken ct = default);

    /// <summary>Claims preparation under current exact command authority before custody is invoked.</summary>
    Task<HoldCommand?> BeginPreparationAsync(HoldCommand expected, CancellationToken ct = default);

    /// <summary>Persists the canonical provider winner without authorizing its transmission.</summary>
    Task<HoldCommand?> SavePreparedAsync(HoldCommand expected, PreparedValueTransfer prepared, CancellationToken ct = default);

    /// <summary>Authorizes first egress under the original liability, cutoff and current exact signing authority.</summary>
    Task<HoldCommand?> AuthorizeSendAsync(HoldCommand expected, CancellationToken ct = default);

    /// <summary>Accounts for an exact finalized transfer with the original command receipt atomically.</summary>
    Task<HoldCommandReceipt?> CompleteRailAsync(HoldCommand expected, HoldTransferEvidence evidence,
        CancellationToken ct = default);

    /// <summary>Preserves exact authoritative no-effect evidence without automatically replacing or releasing liability.</summary>
    Task<HoldCommand?> RecordNoEffectAsync(HoldCommand expected, HoldTransferEvidence evidence,
        CancellationToken ct = default);

    /// <summary>Cancels only a proven unsent/no-effect command under current original-owner authentication.</summary>
    Task<HoldCommand?> CancelRailAsync(string id, HoldRecoveryRequest request, SigningCredentialWitness credential,
        CancellationToken ct = default);

    /// <summary>Replaces a proven no-effect generation with a new disclosed quote, preserving all prior evidence.</summary>
    Task<HoldCommand?> ReplaceRailAsync(HoldCommand expected, HoldRailPlan next, HoldCommandAuthority authority,
        HoldRecoveryRequest request, CancellationToken ct = default);

    /// <summary>Records the actual recipient's exact acceptance consent without authorizing the outbound transfer.</summary>
    Task<HoldCommand?> AdmitAcceptanceAsync(HoldCommand expected, ValueAcceptanceIntent intent,
        SigningAdmissionAuthority authority, HoldContinuationRequest request, CancellationToken ct = default);

    /// <summary>Claims current recipient authority before acceptance custody or first send.</summary>
    Task<HoldCommand?> AdvanceAcceptanceAsync(HoldCommand expected, string target,
        PreparedValueAcceptance? prepared = null, HoldTransferEvidence? evidence = null, CancellationToken ct = default);

    /// <summary>Commits an authorized local reserve open, refund or custody-preserving chain atomically.</summary>
    Task<HoldCommandReceipt?> CommitLocalAsync(HoldCommandIntent intent, HoldCommandAuthority authority,
        CancellationToken ct = default);

    /// <summary>Parks exact workflow opening disclosure without changing liability or reserve counters.</summary>
    Task<HoldCommand?> AdmitWorkflowOpeningAsync(HoldCommandIntent intent, HoldCommandAuthority authority,
        HoldWorkflowMandateDraft draft, CancellationToken ct = default);

    /// <summary>Commits an explicitly accepted opening, reserve draw, mandate and original receipt atomically.</summary>
    Task<HoldCommandReceipt?> CommitWorkflowOpeningAsync(HoldCommand expected, HoldWorkflowOpeningAcceptance acceptance,
        SigningConsentWitness currentAuthority, CancellationToken ct = default);

    /// <summary>Replaces only an unaccepted disclosure, preserving the frozen opening and complete audit.</summary>
    Task<HoldCommand?> ReplaceWorkflowOpeningAsync(HoldCommand expected, HoldWorkflowMandateDraft draft,
        HoldRecoveryRequest request, SigningConsentWitness authority, CancellationToken ct = default);

    /// <summary>Withdraws an unaccepted opening without drawing reserve or requiring a still-live workflow.</summary>
    Task<HoldCommand?> WithdrawWorkflowOpeningAsync(string id, HoldRecoveryRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default);
}
