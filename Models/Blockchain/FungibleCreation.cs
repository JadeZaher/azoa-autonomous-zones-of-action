using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Blockchain;

public sealed record FungibleCreationIntent(string PreparationKey, int Generation, Guid ActorUserId,
    Guid? TenantId, string ChainType, ChainNetwork Network, string CreatorAddress, SigningContext SigningContext,
    string Name, string UnitName, string TotalBaseUnits, int Decimals, bool DefaultFrozen,
    string ManagerAddress, string ReserveAddress, string FreezeAddress, string ClawbackAddress,
    string? Url = null, string? MetadataHashBase64 = null, FungibleCreationQuote? Quote = null);

public sealed record FungibleCreationQuote(string QuoteId, string IntentHash, string ProtocolIdentity,
    string NetworkFeeBaseUnits, string MinimumBalanceBaseUnits, string Currency, int Decimals,
    string PayerAddress, DateTimeOffset ExpiresAt);

public sealed record PreparedFungibleCreation(string PreparationId, string TransactionId,
    string SignedTransactionBase64, string ProtocolIdentity, FungibleCreationIntent Intent);

public sealed record FungibleCreationObservation(string Verdict, string TransactionId,
    string ProtocolIdentity, string? AssetId = null, string? ConfirmedRound = null, string? EvidenceJson = null);

public sealed record FungibleCreationGeneration(int Generation, string IntentJson, string PreparedJson,
    string ObservationJson, string? AuthorityId, string? AuthorityJson);
