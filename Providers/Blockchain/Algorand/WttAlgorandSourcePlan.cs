using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

internal sealed record WttAlgorandSourceStep(string Stage, ulong StorageApplication, ulong StorageIndex,
    string StorageEmitterHex, WttStageReservation Reservation);

internal sealed record WttAlgorandSourcePlan(int Version, string IntentHash, ulong CoreApplication,
    ulong TokenApplication, string GenesisHash, string GenesisId, ulong Asset, string AssetCreator,
    ulong MessageFee, ulong MinimumFee, ulong RegularFee, ulong ApplicationFee,
    ulong StorageFee, IReadOnlyList<WttAlgorandSourceStep> Steps,
    string CoreApprovalHash, string CoreClearHash, string TokenApprovalHash, string TokenClearHash);
