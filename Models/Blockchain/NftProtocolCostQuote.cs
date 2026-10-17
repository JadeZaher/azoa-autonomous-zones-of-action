using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>Separate native-unit limits for network fees, retained account rent and spent protocol levies.</summary>
public sealed record NftProtocolCostLimits(string NetworkFee, string RetainedRent, string ProtocolLevy);

/// <summary>Immutable server-issued cost receipt for one participant, draft and preparation generation.</summary>
public sealed record NftProtocolCostQuote(string QuoteId, Guid ActorUserId, Guid ActingTenantId,
    Guid PayerWalletId, string PayerAddress, string ChainType, ChainNetwork Network,
    string PhysicalNetworkIdentity, string LifecycleId, string DraftHash, string Stage,
    int PreparationGeneration, string TokenStandard, string PolicyEvidenceJson,
    NftProtocolCostLimits Limits, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? QuotedIntentHash = null);

/// <summary>Accepts the exact saved receipt and independent category limits.</summary>
public sealed record NftProtocolCostAuthorization(string QuoteId, string QuoteHash, NftProtocolCostLimits Limits);

/// <summary>Provider-owned economics and execution evidence used to issue a participant-bound receipt.</summary>
public sealed record NftProtocolCostEstimate(string PhysicalNetworkIdentity, string TokenStandard,
    string PolicyEvidenceJson, NftProtocolCostLimits Limits);
