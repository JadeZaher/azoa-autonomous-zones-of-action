using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider : IValueTransferTransactionModule
{
    /// <inheritdoc/>
    public async Task<AZOAResult<PreparedValueTransfer>> PrepareValueTransferAsync(ValueTransferIntent intent, CancellationToken ct)
    {
        if (!TryValueTransferCall(intent, out var target, out var value, out var data))
            return Error<PreparedValueTransfer>("Ethereum value transfer identity is invalid.");
        if (!EthereumAddress.IsNativeToken(intent.AssetId))
        {
            var code = await ContractHasCodeAsync(target, ct);
            if (code.IsError || !code.Result) return Error<PreparedValueTransfer>("ERC20 restitution requires an existing token contract.");
        }
        var prepared = await PrepareCallAsync(intent.PreparationKey, intent.Sender, target, value, data,
            "return reserved units", intent.SigningContext, ct);
        return prepared.IsError || prepared.Result is null ? Error<PreparedValueTransfer>(prepared.Message)
            : Ok(new PreparedValueTransfer(prepared.Result.TransactionId!, prepared.Result.SignedTransactionBase64!, prepared.Result.ProtocolIdentity, intent));
    }

    /// <inheritdoc/>
    public async Task SubmitValueTransferAsync(PreparedValueTransfer prepared, CancellationToken ct)
    {
        var row = await ReadValuePreparationAsync(prepared, ct);
        await SubmitPreparationAsync(row, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ChainConfirmation>> ObserveValueTransferAsync(PreparedValueTransfer prepared, CancellationToken ct)
    {
        var row = await ReadValuePreparationAsync(prepared, ct);
        var result = await ObserveOwnedPreparationAsync(row, ct);
        if (result.IsError || result.Result != ChainConfirmation.Confirmed || EthereumAddress.IsNativeToken(prepared.Intent.AssetId)) return result;
        var receipt = await RpcCallAsync("eth_getTransactionReceipt", new object?[] { prepared.TransactionId }, ct);
        if (receipt.IsError || receipt.IsNullResult || !receipt.Value.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array)
            return Error<ChainConfirmation>("Ethereum token transfer evidence is unavailable.");
        var signature = "0x" + Keccak.CalculateHash("Transfer(address,address,uint256)");
        var from = "0x" + prepared.Intent.Sender[2..].ToLowerInvariant().PadLeft(64, '0');
        var to = "0x" + prepared.Intent.Recipient[2..].ToLowerInvariant().PadLeft(64, '0');
        var amount = BigInteger.Parse(prepared.Intent.QuantityBaseUnits, CultureInfo.InvariantCulture);
        var matches = logs.EnumerateArray().Count(log => TryReadStringProperty(log, "address", out var address)
            && EthereumAddress.AreSame(address, prepared.Intent.AssetId)
            && log.TryGetProperty("topics", out var topics) && topics.ValueKind == JsonValueKind.Array && topics.GetArrayLength() == 3
            && string.Equals(topics[0].GetString(), signature, StringComparison.OrdinalIgnoreCase)
            && string.Equals(topics[1].GetString(), from, StringComparison.OrdinalIgnoreCase)
            && string.Equals(topics[2].GetString(), to, StringComparison.OrdinalIgnoreCase)
            && TryReadStringProperty(log, "data", out var encoded) && EthereumAbi.TryDecodeUInt256(encoded, out var units) && units == amount);
        return matches == 1 ? result : Error<ChainConfirmation>("Finalized ERC20 receipt does not prove the exact returned units.");
    }

    private bool TryValueTransferCall(ValueTransferIntent intent, out string target, out BigInteger value, out byte[] data)
    {
        target = string.Empty; value = BigInteger.Zero; data = [];
        if (intent.ChainType != ChainType || intent.Network != ActiveNetwork || string.IsNullOrWhiteSpace(intent.PreparationKey)
            || !EthereumAddress.TryParse(intent.Sender, out _) || !EthereumAddress.TryParse(intent.Recipient, out var recipient)
            || !BigInteger.TryParse(intent.QuantityBaseUnits, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0 || amount >= (BigInteger.One << 256)) return false;
        if (EthereumAddress.IsNativeToken(intent.AssetId)) { target = recipient; value = amount; return true; }
        if (!EthereumAddress.TryParse(intent.AssetId, out target)) return false;
        return EthereumHex.TryParseData(EthereumAbi.EncodeCall(EthereumAbi.TransferSignature, recipient, amount), out data);
    }

    private async Task<EthereumPreparation> ReadValuePreparationAsync(PreparedValueTransfer prepared, CancellationToken ct)
    {
        if (!TryValueTransferCall(prepared.Intent, out var target, out var value, out var data))
            throw new InvalidOperationException("Ethereum prepared value intent is invalid.");
        var row = await WithPreparationStoreAsync(store => store.GetAsync(EthereumIdentity(prepared.ProtocolIdentity,
            prepared.Intent.Sender.ToLowerInvariant(), prepared.Intent.PreparationKey), ct));
        var fingerprint = EthereumIdentity(prepared.Intent.Sender.ToLowerInvariant(), target.ToLowerInvariant(),
            value.ToString(CultureInfo.InvariantCulture), EthereumHex.Data(data), JsonSerializer.Serialize(prepared.Intent.SigningContext), string.Empty);
        if (row is null || row.CallFingerprint != fingerprint || row.SignedTransactionBase64 != prepared.SignedTransactionBase64
            || row.TransactionId != prepared.TransactionId || row.ProtocolIdentity != prepared.ProtocolIdentity)
            throw new InvalidOperationException("Ethereum prepared value wire differs from its durable authorization.");
        return row;
    }
}
