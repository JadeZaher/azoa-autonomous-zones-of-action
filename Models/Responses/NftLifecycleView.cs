namespace AZOA.WebAPI.Models.Responses;

public sealed record NftLifecycleView(string OperationId, Guid NftId, string State, string Stage,
    string? AssetId, string? TransactionId, string? Detail, string Operation = "",
    AZOA.WebAPI.Models.Blockchain.NftFeeQuote? FeeQuote = null, Guid ActorUserId = default, Guid RecipientUserId = default,
    long Version = 0, int PreparationGeneration = 0, AZOA.WebAPI.Models.Blockchain.NftStageAcceptance? StageAcceptance = null,
    bool NeedsStageAcceptance = false, bool NeedsReplacementCosts = false, bool ReplacementCostsAccepted = false,
    bool NeedsActorContinuation = false, bool NeedsParticipantRenewal = false, Guid? RenewalParticipantUserId = null);

public sealed record NftLifecyclePage(IReadOnlyList<NftLifecycleView> Items, string? NextCursor);
