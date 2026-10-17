using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    /// <inheritdoc cref="IDexTransactionModule.SubmitSwapStageAsync"/>
    public async Task<AZOAResult<IReadOnlyList<string>>> SubmitSwapStageAsync(SwapIntent intent,
        PreparedSwapStage prepared, CancellationToken ct = default)
    {
        var validated = ValidateSignedSwapStage(intent, prepared.Plan, prepared.SignedPayloadJson);
        if (validated.IsError || validated.Result is not { } exact || !exact.TransactionIds.SequenceEqual(prepared.TransactionIds) ||
            exact.SignedPayloadJson != prepared.SignedPayloadJson)
            return Error<IReadOnlyList<string>>("Saved Ethereum wire or transaction identity changed.");
        var saved = ReadEthereumDexPlan(intent, prepared.Plan);
        var quote = ReadDexAcceptedIntent(intent);
        var row = await WithPreparationStoreAsync(store => store.GetAsync(saved.ReservationId, ct));
        if (row is null || row.CallFingerprint != DexCallFingerprint(saved.Call) || row.Network != ActiveNetwork.ToString())
            return Error<IReadOnlyList<string>>("The exact durable Ethereum reservation is missing or changed.");
        if (row.State is "Canceling" or "Canceled" or "Reverted" or "Confirmed")
            return Ok(exact.TransactionIds, "The reserved nonce is canceling or resolved; authoritative observation is required.");
        var chain = await GetChainIdAsync(ct);
        var genesis = await RpcReadAsync("eth_getBlockByNumber", ["0x0", false], ct);
        if (chain.IsError || "eip155:" + Text(chain.Result) != saved.ProtocolIdentity || genesis.IsError || genesis.IsNullResult ||
            !TryReadStringProperty(genesis.Value, "hash", out var actualGenesis) || !SameDexHash(actualGenesis, quote.Deployment.GenesisHash))
            return Error<IReadOnlyList<string>>("Saved Ethereum wire belongs to another physical network.");
        var finalized = await RpcReadAsync("eth_getBlockByNumber", ["finalized", false], ct);
        if (finalized.IsError || finalized.IsNullResult || !TryReadStringProperty(finalized.Value, "hash", out var headHash) ||
            !TryReadQuantityProperty(finalized.Value, "timestamp", out var chainTime))
            throw new HttpRequestException("Finalized Ethereum head is unavailable for submission.");
        if (prepared.Plan.Stage == "Swap" && chainTime >= EthereumDexExecutionCodec.Amount(quote.ExecutionDeadline))
            return Ok(exact.TransactionIds, "The executor deadline elapsed; authoritative observation is required.");
        var consumed = await DexNonceCountAsync(saved.Call.Sender, new { blockHash = headHash, requireCanonical = true }, ct);
        if (consumed is null) throw new HttpRequestException("Finalized sender nonce state is unavailable for submission.");
        if (consumed > EthereumDexExecutionCodec.Amount(saved.Nonce))
            return Ok(exact.TransactionIds, "The reserved nonce is already consumed at finality; authoritative observation is required.");
        var wire = Convert.FromBase64String(JsonSerializer.Deserialize<string>(exact.SignedPayloadJson)!);
        var response = await RpcCallAsync("eth_sendRawTransaction", [EthereumHex.Data(wire)], ct);
        if (response.IsError)
        {
            if (response.ErrorMessage.Contains("already known", StringComparison.OrdinalIgnoreCase) ||
                response.ErrorMessage.Contains("known transaction", StringComparison.OrdinalIgnoreCase))
                return Ok(exact.TransactionIds, "The node already holds the exact saved transaction.");
            if (response.ErrorMessage.Contains("nonce too low", StringComparison.OrdinalIgnoreCase) ||
                response.ErrorMessage.Contains("replacement transaction underpriced", StringComparison.OrdinalIgnoreCase))
                return Ok(exact.TransactionIds, "Another transaction holds the reserved nonce; authoritative finalized observation decides the outcome.");
            throw new HttpRequestException("Ethereum submission requires reconciliation of the original saved wire: " + response.ErrorMessage);
        }
        if (!response.TryGetString(out var echoed) || !SameDexHash(echoed, exact.TransactionIds[0]))
            throw new HttpRequestException("Ethereum node returned a different transaction identity for the saved wire.");
        return Ok(exact.TransactionIds);
    }
}
