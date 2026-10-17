using AZOA.WebAPI.Core;
using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Models.Blockchain;

public sealed record ValueTransferIntent(string ChainType, ChainNetwork Network, string AssetId,
    string Sender, string Recipient, string QuantityBaseUnits, SigningContext SigningContext, string PreparationKey = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ValueTransferCostAuthorization? Cost = null);
public sealed record PreparedValueTransfer(string TransactionId, string SignedTransactionBase64,
    string ProtocolIdentity, ValueTransferIntent Intent, string? RecoveryMetadata = null);
