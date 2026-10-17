using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

internal sealed record WttAlgorandTargetStep(string Stage, string Purpose, ulong StorageApplication,
    ulong StorageIndex, string StorageEmitterHex, WttStageReservation Reservation);

internal sealed record WttAlgorandTargetPlan(int Version, string IntentHash, string GenesisHash, string GenesisId,
    ulong CoreApplication, ulong TokenApplication, string CoreApprovalHash, string CoreClearHash,
    string TokenApprovalHash, string TokenClearHash, ulong? Asset, byte Decimals, string CustodyAddress,
    WttVerifiedProof Transfer, WttVerifiedProof? Attestation, ulong MinimumFee, ulong RegularFee,
    ulong StorageFee, ulong RegistrationVerificationFee, ulong RegistrationCompletionFee,
    ulong RedemptionVerificationFee, ulong RedemptionCompletionFee, IReadOnlyList<WttAlgorandTargetStep> Steps);
