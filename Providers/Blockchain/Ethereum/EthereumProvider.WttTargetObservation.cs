using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    /// <inheritdoc cref="IWttTargetModule.ObserveWttTargetAsync"/>
    public async Task<AZOAResult<WttStageObservation>> ObserveWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs,
        WttStageQuote quote, WttStagePreparation prepared, CancellationToken ct = default)
    {
        _ = RpcHttpClient;
        var plan = ReadWttTargetPlan(intent, proofs, quote);
        var step = plan.Steps.SingleOrDefault(value => value.Stage == prepared.Stage)
            ?? throw new InvalidDataException("Prepared stage is absent from the exact target plan.");
        var row = await ReadWttPreparationAsync(intent, quote, prepared, step, plan.Sender, intent.TargetAuthority.Context, "Target", ct);
        await RequireWttOriginalGenesisAsync(intent.Target.DeploymentIdentity.GenesisHex, ct);
        var observed = await ObservePreparationAsync(row, ct);
        if (observed.IsError) return Error<WttStageObservation>(observed.Message);
        if (observed.Result is not (ChainConfirmation.Confirmed or ChainConfirmation.FailedOnChain))
            return await ObserveWttCancellationAsync(row, prepared, ct)
                ?? PendingWtt(prepared, "Exact target wire has no finalized effect evidence.");
        var receipt = await ReadWttFinalizedReceiptAsync(row, ct);
        if (receipt is null || receipt.Succeeded != (observed.Result == ChainConfirmation.Confirmed))
            return PendingWtt(prepared, "Target finalized receipt identity, status or paid fee requires reconciliation.");
        if (!receipt.Succeeded)
            return Ok(new WttStageObservation("FailedOnChain", prepared.TransactionId, prepared.ProtocolIdentity,
                JsonSerializer.Serialize(new { receipt.BlockHash, receipt.BlockNumber }), NetworkFeePaid: receipt.NetworkFee));
        try
        {
            var block = new { blockHash = receipt.BlockHash, requireCanonical = true };
            var origin = WttPayloadCodec.FixedHex(intent.Asset.OriginAddress, 32);
            string token;
            if (intent.Asset.OriginChain == intent.Target.ProtocolChain)
            {
                if (origin.AsSpan(0, 12).IndexOfAnyExcept((byte)0) >= 0)
                    return PendingWtt(prepared, "Original target token has noncanonical address padding.");
                token = EthereumHex.Data(origin[12..]);
            }
            else
            {
                token = await ReadWttAddressAsync(plan.Deployment.TokenBridge, "wrappedAsset(uint16,bytes32)",
                    [intent.Asset.OriginChain, origin], block, ct);
                if (await ReadWttUIntAsync(token, "chainId()", [], block, ct) != intent.Asset.OriginChain
                    || !EthereumHex.TryParseData(await ReadWttCallAsync(token, "nativeContract()", [], block, ct), out var actualOrigin)
                    || !actualOrigin.AsSpan().SequenceEqual(origin))
                    return PendingWtt(prepared, "Finalized wrapped asset does not bind the original token.");
            }
            if (plan.AssetAddress is not null && !EthereumAddress.AreSame(plan.AssetAddress, token)
                || await ReadWttUIntAsync(token, "decimals()", [], block, ct) != plan.AssetDecimals)
                return PendingWtt(prepared, "Finalized target representation differs from its frozen identity or precision.");
            var asset = new WttAssetDescriptor(token, false, plan.AssetDecimals, intent.Asset.OriginChain,
                intent.Asset.OriginAddress, intent.Asset.OriginDecimals, "", "", plan.AssetBehaviorIdentity);
            await RequireWttExecutionCodeAsync(intent.Target, asset, plan.Deployment, plan.AssetPin, receipt,
                prepared.Stage == "Register", ct);
            if (prepared.Stage == "Register")
                return Ok(new WttStageObservation("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity,
                    JsonSerializer.Serialize(new { receipt.BlockHash, receipt.BlockNumber, assetAddress = token,
                        attestationBodyDigest = plan.AttestationBodyDigest }), NetworkFeePaid: receipt.NetworkFee));
            var transfer = WttProofBinding.Transfer(intent, proofs.Transfer);
            if (await ReadWttUIntAsync(plan.Deployment.TokenBridge, "isTransferCompleted(bytes32)",
                    [Convert.FromHexString(transfer.BodyDigest)], block, ct) != 1
                || !receipt.Receipt.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array
                || logs.EnumerateArray().Count(log => WttEthereumProtocol.IsRedemption(log, plan.Deployment.TokenBridge,
                    prepared.TransactionId, receipt.BlockHash, transfer)) != 1)
                return PendingWtt(prepared, "Target receipt does not uniquely prove this canonical body's redemption.");
            var total = WttPayloadCodec.ParseAmount(WttPayloadCodec.Denormalize(intent.Amount.Amount, plan.AssetDecimals));
            var fee = EthereumAddress.AreSame(intent.RecipientAddress, plan.Sender) ? BigInteger.Zero
                : WttPayloadCodec.ParseAmount(WttPayloadCodec.Denormalize(intent.Amount.Fee, plan.AssetDecimals));
            var payouts = new List<(string Recipient, BigInteger Amount)> { (intent.RecipientAddress, total - fee) };
            if (fee > 0) payouts.Add((plan.Sender, fee));
            if (intent.UnwrapNative)
            {
                var weth = await ReadWttAddressAsync(plan.Deployment.TokenBridge, "WETH()", [], block, ct);
                if (!EthereumAddress.AreSame(token, weth) || intent.Asset.OriginChain != intent.Target.ProtocolChain)
                    return PendingWtt(prepared, "Unwrapped redemption does not use original WETH.");
                foreach (var amount in payouts.GroupBy(payout => payout.Amount))
                    if (logs.EnumerateArray().Count(log => WttTokenEvent(log, token, plan.Deployment.TokenBridge, null,
                        amount.Key, prepared.TransactionId, receipt.BlockHash, "Withdrawal")) != amount.Count())
                        return PendingWtt(prepared, "Native redemption withdrawal evidence differs from its exact payouts.");
            }
            else
            {
                var from = intent.Asset.OriginChain == intent.Target.ProtocolChain ? plan.Deployment.TokenBridge : "0x" + new string('0', 40);
                foreach (var payout in payouts)
                    if (logs.EnumerateArray().Count(log => WttTokenEvent(log, token, from, payout.Recipient,
                        payout.Amount, prepared.TransactionId, receipt.BlockHash, "Transfer")) != 1)
                        return PendingWtt(prepared, "Target token transfers differ from the exact recipient and relayer payouts.");
            }
            return Ok(new WttStageObservation("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity,
                JsonSerializer.Serialize(new { receipt.BlockHash, receipt.BlockNumber, bodyDigest = transfer.BodyDigest,
                    assetAddress = token, recipient = intent.RecipientAddress, recipientAmount = (total - fee).ToString(CultureInfo.InvariantCulture),
                    relayer = plan.Sender, relayerFee = fee.ToString(CultureInfo.InvariantCulture), native = intent.UnwrapNative }),
                NetworkFeePaid: receipt.NetworkFee));
        }
        catch (InvalidDataException exception) { return PendingWtt(prepared, exception.Message); }
        catch (FormatException exception) { return PendingWtt(prepared, exception.Message); }
    }
}
