using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.ClientSigning;
using Nethereum.Model;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider : IClientSignedTransferModule
{
    /// <summary>The fields the unsigned EIP-1559 payload commits to, kept so observation never re-parses RLP.</summary>
    private sealed record EthereumClientPlan(string ChainId, string Nonce, string PriorityFee, string MaxFee, string GasLimit,
        string Target, string Value, string Data);

    /// <inheritdoc/>
    public async Task<AZOAResult<ClientSigningPlan>> PlanClientSignedTransferAsync(ValueTransferIntent intent, CancellationToken ct)
    {
        if (!TryValueTransferCall(intent, out var target, out var value, out var data) || EthereumAddress.AreSame(intent.Sender, intent.Recipient))
            return Error<ClientSigningPlan>("A valid sender, distinct recipient, asset and positive amount are required.");
        if (!EthereumAddress.IsNativeToken(intent.AssetId))
        {
            var code = await ContractHasCodeAsync(target, ct);
            if (code.IsError || !code.Result) return Error<ClientSigningPlan>("ERC-20 transfers require an existing token contract.");
        }
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError) return Error<ClientSigningPlan>(chain.Message);
        var fees = await GetFeeDataAsync(ct);
        if (fees.IsError || fees.Result is null) return Error<ClientSigningPlan>(fees.Message);
        var sender = intent.Sender.ToLowerInvariant();
        var gas = await EstimateGasAsync(sender, target, value, data, "plan a client-signed transfer", ct);
        if (gas.IsError) return Error<ClientSigningPlan>(gas.Message);
        var pending = await RpcReadAsync("eth_getTransactionCount", new object?[] { sender, "pending" }, ct);
        if (pending.IsError || !pending.TryGetString(out var nonceText) || !EthereumHex.TryParseQuantity(nonceText, out var nonce))
            return Error<ClientSigningPlan>("The sender's pending nonce is unavailable.");
        var call = new EthereumClientPlan(chain.Result.ToString(CultureInfo.InvariantCulture), nonce.ToString(CultureInfo.InvariantCulture),
            fees.Result.MaxPriorityFeePerGas.ToString(CultureInfo.InvariantCulture), fees.Result.MaxFeePerGas.ToString(CultureInfo.InvariantCulture),
            gas.Result.ToString(CultureInfo.InvariantCulture), target.ToLowerInvariant(), value.ToString(CultureInfo.InvariantCulture), EthereumHex.Data(data));
        return Ok(new ClientSigningPlan(1, ChainType, ActiveNetwork.ToString(), "eip155:" + call.ChainId, ClientSigningFormats.Ethereum,
            sender, Convert.ToBase64String(ClientTransaction(call).GetRLPEncodedRaw()), CanonicalJson.Hash(intent),
            Nonce: call.Nonce, RecoveryJson: JsonSerializer.Serialize(call)));
    }

    /// <inheritdoc/>
    public AZOAResult<string> VerifyClientSignedTransfer(ClientSigningPlan plan, string signedTransactionBase64)
    {
        try
        {
            ReadClientPlan(plan);
            return Ok(EthereumSignedEnvelope.Verify(Convert.FromBase64String(signedTransactionBase64),
                Convert.FromBase64String(plan.UnsignedPayloadBase64), plan.Signer));
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or InvalidOperationException)
        {
            return Error<string>("Signed bytes are not the saved EIP-1559 transaction signed by its sender.");
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<string>> SubmitClientSignedTransferAsync(ClientSigningPlan plan, string signedTransactionBase64, CancellationToken ct)
    {
        var verified = VerifyClientSignedTransfer(plan, signedTransactionBase64);
        if (verified.IsError) return verified;
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError || plan.ProtocolIdentity != "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture))
            return Error<string>("The saved plan belongs to another Ethereum chain.");
        var response = await RpcCallAsync("eth_sendRawTransaction",
            new object?[] { EthereumHex.Data(Convert.FromBase64String(signedTransactionBase64)) }, ct);
        if (response.IsError)
            return AZOAResult<string>.FailureWithCode("The node did not accept the broadcast; reconcile the saved transaction.",
                ClientSigningCodes.BroadcastUnconfirmed);
        return response.TryGetString(out var hash) && string.Equals(hash, verified.Result, StringComparison.OrdinalIgnoreCase) ? verified
            : AZOAResult<string>.FailureWithCode("The node returned a different transaction hash.", ClientSigningCodes.BroadcastUnconfirmed);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ClientSigningObservation>> ObserveClientSignedTransferAsync(ClientSigningPlan plan, string? transactionId,
        CancellationToken ct)
    {
        var call = ReadClientPlan(plan);
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError || plan.ProtocolIdentity != "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture))
            return Error<ClientSigningObservation>("The saved plan belongs to another Ethereum chain.");
        var nonce = BigInteger.Parse(call.Nonce, CultureInfo.InvariantCulture);
        var pending = Ok(new ClientSigningObservation(ClientSigningVerdicts.Pending, transactionId));
        string? consumer = null;
        if (transactionId is not null)
        {
            var transaction = await RpcCallAsync("eth_getTransactionByHash", new object?[] { transactionId }, ct);
            if (transaction.IsError) return pending;
            if (!transaction.IsNullResult)
            {
                var tx = transaction.Value;
                if (!TryReadStringProperty(tx, "from", out var from) || !EthereumAddress.AreSame(from, plan.Signer)
                    || !TryReadQuantityProperty(tx, "nonce", out var txNonce) || txNonce != nonce)
                    return Error<ClientSigningObservation>("The transaction is not from the plan signer at the plan nonce.");
                if (SamePayload(tx, call))
                    return await FinalizedOutcomeAsync(transactionId, pending, ct);
                consumer = transactionId;
            }
        }
        var finalizedNonce = await RpcReadAsync("eth_getTransactionCount", new object?[] { plan.Signer, "finalized" }, ct);
        if (finalizedNonce.IsError || !finalizedNonce.TryGetString(out var countText) || !EthereumHex.TryParseQuantity(countText, out var count)
            || count <= nonce)
            return pending;
        // TransactionId names the consumer only when a found, different transaction holds the nonce; null means unattributed.
        return Ok(new ClientSigningObservation(ClientSigningVerdicts.NonceConsumed, consumer, consumer is null
            ? "The plan nonce is finalized, but the given transaction is not on chain, so the consumer is unattributed."
            : "The plan nonce is finalized by a different transaction from the same signer."));
    }

    private async Task<AZOAResult<ClientSigningObservation>> FinalizedOutcomeAsync(string transactionId,
        AZOAResult<ClientSigningObservation> pending, CancellationToken ct)
    {
        var receipt = await RpcCallAsync("eth_getTransactionReceipt", new object?[] { transactionId }, ct);
        if (receipt.IsError || receipt.IsNullResult
            || !TryReadQuantityProperty(receipt.Value, "blockNumber", out var blockNumber)
            || !TryReadStringProperty(receipt.Value, "blockHash", out var blockHash)
            || !TryReadQuantityProperty(receipt.Value, "status", out var status) || status < 0 || status > 1) return pending;
        var block = await RpcCallAsync("eth_getBlockByNumber", new object?[] { EthereumHex.Quantity(blockNumber), false }, ct);
        var finalized = await RpcCallAsync("eth_getBlockByNumber", new object?[] { "finalized", false }, ct);
        if (block.IsError || finalized.IsError || block.IsNullResult || finalized.IsNullResult
            || !TryReadStringProperty(block.Value, "hash", out var canonical) || !string.Equals(canonical, blockHash, StringComparison.OrdinalIgnoreCase)
            || !TryReadQuantityProperty(finalized.Value, "number", out var finalizedNumber) || finalizedNumber < blockNumber) return pending;
        return Ok(new ClientSigningObservation(status == 1 ? ClientSigningVerdicts.Confirmed : ClientSigningVerdicts.FailedOnChain, transactionId));
    }

    private static bool SamePayload(JsonElement tx, EthereumClientPlan call)
        => TryReadStringProperty(tx, "to", out var to) && EthereumAddress.AreSame(to, call.Target)
            && TryReadQuantityProperty(tx, "value", out var value) && value == BigInteger.Parse(call.Value, CultureInfo.InvariantCulture)
            && TryReadStringProperty(tx, "input", out var input) && string.Equals(input, call.Data, StringComparison.OrdinalIgnoreCase)
            && TryReadQuantityProperty(tx, "chainId", out var chainId) && chainId == BigInteger.Parse(call.ChainId, CultureInfo.InvariantCulture);

    private EthereumClientPlan ReadClientPlan(ClientSigningPlan plan)
    {
        if (plan.Version != 1 || plan.ChainType != ChainType || plan.Network != ActiveNetwork.ToString() || plan.Format != ClientSigningFormats.Ethereum
            || plan.RecoveryJson is null || plan.Nonce is null)
            throw new InvalidOperationException("The saved plan is not an Ethereum client-signing plan for this network.");
        var call = JsonSerializer.Deserialize<EthereumClientPlan>(plan.RecoveryJson)
            ?? throw new InvalidOperationException("The saved Ethereum plan fields are missing.");
        if (call.Nonce != plan.Nonce || "eip155:" + call.ChainId != plan.ProtocolIdentity
            || !ClientTransaction(call).GetRLPEncodedRaw().AsSpan().SequenceEqual(Convert.FromBase64String(plan.UnsignedPayloadBase64)))
            throw new InvalidOperationException("The saved Ethereum plan fields do not reproduce its unsigned bytes.");
        return call;
    }

    private static Transaction1559 ClientTransaction(EthereumClientPlan call)
        => new(BigInteger.Parse(call.ChainId, CultureInfo.InvariantCulture), BigInteger.Parse(call.Nonce, CultureInfo.InvariantCulture),
            BigInteger.Parse(call.PriorityFee, CultureInfo.InvariantCulture), BigInteger.Parse(call.MaxFee, CultureInfo.InvariantCulture),
            BigInteger.Parse(call.GasLimit, CultureInfo.InvariantCulture), call.Target, BigInteger.Parse(call.Value, CultureInfo.InvariantCulture),
            call.Data, accessList: null);
}
