using System.Text.Json.Serialization;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>One immutable ordinary asset transfer and its exact custody role.</summary>
public sealed record ExchangeWireLeg(string AssetId, string QuantityBaseUnits, string Sender,
    string Recipient, string SignerRole, SigningContext SigningContext);

/// <summary>Project identity whose derived custody key may sign an exact treasury leg.</summary>
public sealed record ExchangeWireTreasury(Guid TenantUserId, string ProjectReference);

/// <summary>Participant acceptance of the asset it receives, funded by the fee payer, placed ahead of the legs in the same group.</summary>
public sealed record ExchangeWireReadiness(string AssetId, string Recipient, string FundingBaseUnits, SigningContext RecipientContext);

/// <summary>Exact group economics, physical chain and accepted fee cap for one source generation.</summary>
public sealed record ExchangeWireIntent(int Version, string SourceId, long Generation, string ChainType,
    ChainNetwork Network, string ProtocolIdentity, IReadOnlyList<ExchangeWireLeg> Legs,
    string FeePayerAddress, SigningContext FeePayerContext, string MaximumNetworkFeeBaseUnits,
    ExchangeWireTreasury? Treasury = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExchangeWireReadiness? Readiness = null);

/// <summary>Complete canonical signed group persisted before the source authorizes first egress.</summary>
public sealed record PreparedExchangeWire(ExchangeWireIntent Intent, string PreparationId, string GroupId,
    IReadOnlyList<string> TransactionIds, IReadOnlyList<string> SignedTransactionsBase64);

/// <summary>Authoritative complete-group verdict bound to the original prepared bytes.</summary>
public sealed record ExchangeWireObservation(string Verdict, string PreparationId, string GroupId,
    string PreparedHash, string ProtocolIdentity, string? FinalizedPosition = null);
