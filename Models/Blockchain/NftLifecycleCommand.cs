using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>Immutable authenticated parties and request metadata captured before NFT signing.</summary>
public sealed record NftLifecycleCommand(Guid Actor, Guid Recipient, Guid ActorWalletId, Guid RecipientWalletId,
    string ActorAddress, string RecipientAddress, UserNFTMintModel Metadata, long AuthorityVersion,
    Guid ActingTenantId, string? SourceAddress = null, Guid? SourceUserId = null,
    string? SourceLifecycleId = null, string? Reason = null, NftFeeQuote? FeeQuote = null,
    NftFeeAuthorization? FeeAuthorization = null,
    AZOA.WebAPI.Models.Actions.ActionEffectAuthority? ActionAuthority = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    NftProtocolCostQuote? ProtocolCostQuote = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    NftWorkflowCostAcceptanceReceipt? WorkflowCostAcceptance = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    AZOA.WebAPI.Models.Signing.SigningAdmissionAuthority? ActorAuthority = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? PlatformAuthorityAddress = null);
