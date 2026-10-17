using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>Immutable participant authority for one exact NFT stage and preparation generation.</summary>
public sealed record NftStageConsent(string LifecycleId, string Stage, int Generation, string IntentHash,
    SigningAdmissionAuthority Authority, DateTimeOffset AcceptedAt, NftStageAcceptance? Request = null);

/// <summary>Explicit acceptance of the exact lifecycle version and participant stage displayed to the caller.</summary>
public sealed record NftStageAcceptance(Guid RequestId, long ExpectedVersion, string State, string Stage, int Generation);
