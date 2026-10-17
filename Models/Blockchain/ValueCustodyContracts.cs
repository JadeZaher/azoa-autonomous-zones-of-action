using AZOA.WebAPI.Core;
using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>Exact liability movement whose current mechanics and native costs must be established.</summary>
public sealed record ValueCustodyQuoteRequest(string ChainType, ChainNetwork Network, string AssetId,
    string Sender, string Recipient, string QuantityBaseUnits, string HoldingAddress);

/// <summary>Current immutable protocol/mechanics proof and separate transfer/acceptance native budgets.</summary>
public sealed record ValueCustodyQuote(int Version, string QuoteId, ValueCustodyQuoteRequest Request,
    string ProtocolIdentity, string AssetAuthorityHash, string NativeCurrency, int NativeDecimals,
    string TransferNetworkFeeBaseUnits, bool RecipientReady, string AcceptanceNetworkFeeBaseUnits,
    string AcceptanceMinimumBalanceBaseUnits, DateTimeOffset QuotedAt, DateTimeOffset ExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ValueCustodyCostBreakdown? TransferCosts = null);

/// <summary>Explicit native cost categories and their immutable payer/refund destination.</summary>
public sealed record ValueCustodyCostBreakdown(string PayerAddress, string RefundRecipientAddress,
    string NetworkFeeBaseUnits, string LockedBalanceBaseUnits, string NonrefundableProtocolCostBaseUnits);

/// <summary>Accepted exact stage budget, bound to the original durable liability command.</summary>
public sealed record ValueTransferCostAuthorization(int Version, string CommandId, string Stage, long Generation,
    ValueCustodyQuote Quote, string PayerAddress, string MaximumNetworkFeeBaseUnits);

/// <summary>Actual recipient's saved authority for an account acceptance stage.</summary>
public sealed record ValueAcceptanceIntent(string ChainType, ChainNetwork Network, string AssetId,
    string Recipient, string PreparationKey, SigningContext SigningContext, ValueTransferCostAuthorization Cost);

/// <summary>Journaled exact recipient acceptance wire; never a transfer or new participant authorization.</summary>
public sealed record PreparedValueAcceptance(string TransactionId, string SignedTransactionBase64,
    string ProtocolIdentity, ValueAcceptanceIntent Intent, string? RecoveryMetadata = null);
