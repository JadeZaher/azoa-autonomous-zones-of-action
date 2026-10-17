using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>The original requester's fresh direct authority for later NFT effects; the immutable command keeps the original.</summary>
public sealed record NftActorContinuation(NftStageAcceptance Request, SigningAdmissionAuthority Authority, DateTimeOffset ContinuedAt);
