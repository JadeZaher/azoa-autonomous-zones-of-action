using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    /// <inheritdoc cref="IWttSourceModule.ObserveWttSourceAsync"/>
    public async Task<AZOAResult<WttStageObservation>> ObserveWttSourceAsync(WttOperationIntent intent, WttStageQuote quote,
        WttStagePreparation prepared, CancellationToken ct = default)
    {
        var row = await ReadWttSourcePreparationAsync(intent, quote, prepared, ct);
        await RequireWttOriginalGenesisAsync(intent.Source.DeploymentIdentity.GenesisHex, ct);
        var observed = await ObservePreparationAsync(row, ct);
        if (observed.IsError) return Error<WttStageObservation>(observed.Message);
        if (observed.Result is not (ChainConfirmation.Confirmed or ChainConfirmation.FailedOnChain))
        {
            var canceled = await ObserveWttCancellationAsync(row, prepared, ct);
            if (canceled is not null) return canceled;
            return PendingWtt(prepared, "Exact nonce-owned WTT transaction has no finalized effect evidence.");
        }
        var receipt = await ReadWttFinalizedReceiptAsync(row, ct);
        if (receipt is null) return PendingWtt(prepared, "Finalized WTT receipt identity or fee evidence is incomplete.");
        if (receipt.Succeeded != (observed.Result == ChainConfirmation.Confirmed))
            return PendingWtt(prepared, "WTT receipt status changed while collecting finalized evidence.");
        if (observed.Result == ChainConfirmation.FailedOnChain)
            return Ok(new WttStageObservation("FailedOnChain", prepared.TransactionId, prepared.ProtocolIdentity,
                JsonSerializer.Serialize(new { receipt.BlockHash, receipt.BlockNumber }), NetworkFeePaid: receipt.NetworkFee));
        var plan = ReadWttSourcePlan(intent, quote);
        try { await RequireWttExecutionCodeAsync(intent.Source, intent.Asset, plan.Deployment, plan.AssetPin, receipt, false, ct); }
        catch (InvalidDataException exception) { return PendingWtt(prepared, exception.Message); }
        if (!receipt.Receipt.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array)
            return PendingWtt(prepared, "WTT receipt logs are unavailable.");
        if (prepared.Stage is "ResetAllowance" or "Approve")
        {
            var amount = prepared.Stage == "ResetAllowance" ? BigInteger.Zero : WttPayloadCodec.ParseAmount(intent.Amount.Debited);
            if (logs.EnumerateArray().Count(log => WttTokenEvent(log, intent.Asset.TokenAddress, intent.SourceAuthority.Address,
                intent.Source.TokenBridge, amount, prepared.TransactionId, receipt.BlockHash, "Approval")) != 1)
                return PendingWtt(prepared, "Successful receipt does not prove the exact ERC20 allowance change.");
            return Ok(new WttStageObservation("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity,
                JsonSerializer.Serialize(new { receipt.BlockHash, receipt.BlockNumber, allowance = amount.ToString(CultureInfo.InvariantCulture) }),
                NetworkFeePaid: receipt.NetworkFee));
        }
        if (prepared.Stage == "Transfer" && !intent.Asset.NativeAsset)
        {
            var amount = WttPayloadCodec.ParseAmount(intent.Amount.Debited);
            if (logs.EnumerateArray().Count(log => WttTokenEvent(log, intent.Asset.TokenAddress, intent.SourceAuthority.Address,
                    intent.Source.TokenBridge, amount, prepared.TransactionId, receipt.BlockHash, "Transfer")) != 1
                || intent.Asset.OriginChain != intent.Source.ProtocolChain && logs.EnumerateArray().Count(log =>
                    WttTokenEvent(log, intent.Asset.TokenAddress, intent.Source.TokenBridge, "0x" + new string('0', 40),
                        amount, prepared.TransactionId, receipt.BlockHash, "Transfer")) != 1)
                return PendingWtt(prepared, "Source token debit or wrapped-token burn evidence differs from the authorized amount.");
        }
        if (prepared.Stage == "Transfer" && intent.Asset.NativeAsset
            && logs.EnumerateArray().Count(log => WttTokenEvent(log, intent.Asset.TokenAddress, intent.Source.TokenBridge, null,
                WttPayloadCodec.ParseAmount(intent.Amount.Debited), prepared.TransactionId, receipt.BlockHash, "Deposit")) != 1)
            return PendingWtt(prepared, "Native source value lacks the exact bridge-owned wrapped deposit evidence.");
        var payload = prepared.Stage == "Attest"
            ? WttPayloadCodec.EncodeAttestation(new(intent.Asset.OriginAddress, intent.Asset.OriginChain, intent.Asset.Decimals,
                intent.Asset.SymbolHex, intent.Asset.NameHex))
            : WttPayloadCodec.EncodeTransfer(new(intent.Amount.Amount, intent.Asset.OriginAddress, intent.Asset.OriginChain,
                intent.RecipientProtocolAddress, intent.Target.ProtocolChain, intent.Amount.Fee));
        var publications = new List<WttPublication>();
        foreach (var log in logs.EnumerateArray())
            if (WttEthereumProtocol.TryPublication(log, prepared.ProtocolIdentity, intent.Source.CoreBridge, intent.Source.TokenBridge,
                prepared.TransactionId, receipt.BlockHash, receipt.Timestamp, intent.Source.ProtocolChain, intent.Nonce,
                intent.ConsistencyLevel, payload, out var publication)) publications.Add(publication!);
        if (publications.Count != 1) return PendingWtt(prepared, "Receipt does not uniquely prove the exact authorized WTT publication.");
        return Ok(new WttStageObservation("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity,
            JsonSerializer.Serialize(new { receipt.BlockHash, receipt.BlockNumber }), publications[0], receipt.NetworkFee));
    }

    private Task<EthereumFinalizedReceipt?> ReadWttFinalizedReceiptAsync(EthereumPreparation row, CancellationToken ct)
        => ReadFinalizedReceiptAsync(row.TransactionId!, ct);

    private async Task<AZOAResult<WttStageObservation>?> ObserveWttCancellationAsync(EthereumPreparation row,
        WttStagePreparation prepared, CancellationToken ct)
    {
        foreach (var cancellation in JsonSerializer.Deserialize<List<EthereumCancellation>>(row.RecoveryCandidatesJson) ?? [])
        {
            var candidate = CancellationPreparation(row, cancellation);
            var canceled = await ObservePreparationAsync(candidate, ct);
            if (canceled.IsError) return Error<WttStageObservation>(canceled.Message);
            if (canceled.Result is not (ChainConfirmation.Confirmed or ChainConfirmation.FailedOnChain)) continue;
            var receipt = await ReadWttFinalizedReceiptAsync(candidate, ct);
            if (receipt is null || receipt.Succeeded != (canceled.Result == ChainConfirmation.Confirmed))
                return PendingWtt(prepared, "The winning cancellation receipt requires reconciliation.");
            return Ok(new WttStageObservation("FailedOnChain", prepared.TransactionId, prepared.ProtocolIdentity,
                JsonSerializer.Serialize(new { cancelledOriginal = prepared.TransactionId, winningNonceTransaction = cancellation.TransactionId,
                    receipt.BlockHash, receipt.BlockNumber }), NetworkFeePaid: receipt.NetworkFee));
        }
        return null;
    }

    private async Task RequireWttExecutionCodeAsync(WttNetworkDescriptor network, WttAssetDescriptor asset,
        WttEvmDeployment deployment, WttEvmAssetPin assetPin, EthereumFinalizedReceipt receipt, bool createdAsset, CancellationToken ct)
    {
        foreach (var hash in new[] { receipt.ParentHash, receipt.BlockHash })
        {
            var block = new { blockHash = hash, requireCanonical = true };
            await RequireWttProxyAsync(deployment.Core, deployment.CoreProxyHash, deployment.CoreImplementationHash, block, ct);
            await RequireWttProxyAsync(deployment.TokenBridge, deployment.TokenProxyHash, deployment.TokenImplementationHash, block, ct);
            if (!createdAsset || hash == receipt.BlockHash) await RequireWttAssetCodeAtAsync(asset, deployment, assetPin, block, ct);
        }
        var addresses = createdAsset ? new[] { network.CoreBridge, network.TokenBridge }
            : new[] { network.CoreBridge, network.TokenBridge, asset.TokenAddress };
        var topics = new[] { "0x" + Keccak.CalculateHash("Upgraded(address)"), "0x" + Keccak.CalculateHash("BeaconUpgraded(address)"),
            "0x" + Keccak.CalculateHash("ContractUpgraded(address,address)") };
        var upgrades = await RpcReadAsync("eth_getLogs", [new { blockHash = receipt.BlockHash, address = addresses,
            topics = new object[] { topics } }], ct);
        if (upgrades.IsError || upgrades.IsNullResult || upgrades.Value.ValueKind != JsonValueKind.Array || upgrades.Value.GetArrayLength() != 0)
            throw new InvalidDataException("Execution-block upgrades require position-specific code reconciliation before WTT confirmation.");
    }

    private static bool WttTokenEvent(JsonElement log, string token, string owner, string? spender, BigInteger amount,
        string transactionId, string blockHash, string eventName)
        => EthereumTokenEventMatches(log, token, owner, spender, amount, transactionId, blockHash, eventName);

    private static AZOAResult<WttStageObservation> PendingWtt(WttStagePreparation prepared, string detail)
        => AZOAResult<WttStageObservation>.Success(new("Unknown", prepared.TransactionId, prepared.ProtocolIdentity, Detail: detail));
}
