using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Algorand;
using Algorand.Algod.Model.Transactions;
using AlgorandEncoder = Algorand.Utils.Encoder;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.ClientSigning;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider : IClientSignedTransferModule
{
    /// <inheritdoc/>
    public async Task<AZOAResult<ClientSigningPlan>> PlanClientSignedTransferAsync(ValueTransferIntent intent, CancellationToken ct)
    {
        if (intent.ChainType != ChainType || intent.Network != ActiveNetwork || string.IsNullOrWhiteSpace(intent.PreparationKey)
            || !ValidateAddressFormat(intent.Sender) || !ValidateAddressFormat(intent.Recipient) || intent.Sender == intent.Recipient
            || !UInt64Text.TryParse(intent.AssetId, out var asset) || asset == 0
            || !UInt64Text.TryParse(intent.QuantityBaseUnits, out var amount) || amount == 0)
            return AZOAResult<ClientSigningPlan>.Failure("A numeric ASA, distinct valid accounts and a positive amount are required.");
        var parameters = await ValidatedTransactionParametersAsync(ct);
        var hash = CanonicalJson.Hash(intent);
        var transaction = new AssetTransferTransaction
        {
            Sender = new Address(intent.Sender), AssetReceiver = new Address(intent.Recipient), XferAsset = asset, AssetAmount = amount,
            Note = Encoding.UTF8.GetBytes(hash),
        };
        ApplySuggestedParams(transaction, parameters);
        transaction.Fee = AvmFee(transaction, parameters);
        transaction.Fee = AvmFee(transaction, parameters);
        return AZOAResult<ClientSigningPlan>.Success(new(1, ChainType, ActiveNetwork.ToString(), "algorand:" + parameters.GenesisHashB64,
            ClientSigningFormats.Algorand, intent.Sender, Convert.ToBase64String(AlgorandEncoder.EncodeToMsgPackOrdered(transaction)), hash,
            transaction.TxID(), checked((long)transaction.LastValid!.Value)));
    }

    /// <inheritdoc/>
    public AZOAResult<string> VerifyClientSignedTransfer(ClientSigningPlan plan, string signedTransactionBase64)
    {
        try
        {
            var expected = PlannedClientTransaction(plan);
            var signed = AlgorandSignedEnvelope.MatchExact(signedTransactionBase64, expected, plan.Signer);
            return signed.Tx.TxID() == plan.PredictedTransactionId ? AZOAResult<string>.Success(plan.PredictedTransactionId!)
                : AZOAResult<string>.Failure("Signed Algorand transaction identity differs from the saved plan.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException
            or Newtonsoft.Json.JsonException)
        {
            return AZOAResult<string>.Failure("Signed bytes are not the saved Algorand transaction signed by its sender.");
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<string>> SubmitClientSignedTransferAsync(ClientSigningPlan plan, string signedTransactionBase64, CancellationToken ct)
    {
        var verified = VerifyClientSignedTransfer(plan, signedTransactionBase64);
        if (verified.IsError) return verified;
        var parameters = await ValidatedTransactionParametersAsync(ct);
        if (plan.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
            return AZOAResult<string>.Failure("The saved plan belongs to another Algorand genesis.");
        if (parameters.LastRound > plan.ValidThroughHeight)
            return AZOAResult<string>.FailureWithCode("The plan's validity window has passed; it can no longer be broadcast.", ClientSigningCodes.Expired);
        using var content = new ByteArrayContent(Convert.FromBase64String(signedTransactionBase64));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-binary");
        using var response = await AlgodHttpClient.PostAsync("/v2/transactions", content, ct);
        if (!response.IsSuccessStatusCode)
            return AZOAResult<string>.FailureWithCode($"Algod refused the broadcast ({(int)response.StatusCode}); reconcile the saved transaction.",
                ClientSigningCodes.BroadcastUnconfirmed);
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return AlgorandReceiptFields.String(result.RootElement, "txId", verified.Result!) ? verified
            : AZOAResult<string>.FailureWithCode("Algod returned a different transaction identity.", ClientSigningCodes.BroadcastUnconfirmed);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ClientSigningObservation>> ObserveClientSignedTransferAsync(ClientSigningPlan plan, string? transactionId,
        CancellationToken ct)
    {
        var planned = plan.PredictedTransactionId ?? string.Empty;
        if (transactionId is not null && transactionId != planned)
            return AZOAResult<ClientSigningObservation>.Failure("An Algorand id is the hash of the saved bytes; a different id is not this plan.");
        var transaction = PlannedClientTransaction(plan);
        var parameters = await ValidatedTransactionParametersAsync(ct);
        if (plan.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
            return AZOAResult<ClientSigningObservation>.Failure("The saved plan belongs to another Algorand genesis.");
        var pending = AZOAResult<ClientSigningObservation>.Success(new(ClientSigningVerdicts.Pending, planned));
        if (IndexerHttpClient is not { } indexer) return pending;
        using var response = await indexer.GetAsync($"/v2/transactions?txid={Uri.EscapeDataString(planned)}", ct);
        if (!response.IsSuccessStatusCode) return pending;
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("current-round", out var round) || !round.TryGetUInt64(out var indexedRound)
            || !root.TryGetProperty("transactions", out var found) || found.ValueKind != JsonValueKind.Array || found.GetArrayLength() > 1
            || root.TryGetProperty("next-token", out var token) && token.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(token.GetString()))
            return pending;
        if (found.GetArrayLength() == 0)
            return indexedRound > transaction.LastValid && (ulong)parameters.LastRound > transaction.LastValid
                ? AZOAResult<ClientSigningObservation>.Success(new(ClientSigningVerdicts.Expired, null,
                    "The validity window passed and the indexed chain holds no transaction with the plan's id."))
                : pending;
        var tx = found[0];
        if (!AlgorandReceiptFields.String(tx, "id", planned) || !AlgorandReceiptFields.String(tx, "sender", plan.Signer)
            || !AlgorandReceiptFields.String(tx, "tx-type", "axfer") || !AlgorandReceiptFields.Number(tx, "fee", transaction.Fee ?? 0UL)
            || !tx.TryGetProperty("confirmed-round", out var confirmed) || !confirmed.TryGetUInt64(out var confirmedRound)
            || confirmedRound < transaction.FirstValid || confirmedRound > transaction.LastValid || indexedRound < confirmedRound
            || !tx.TryGetProperty("asset-transfer-transaction", out var body) || body.ValueKind != JsonValueKind.Object
            || transaction is not AssetTransferTransaction transfer
            || !AlgorandReceiptFields.Number(body, "asset-id", transfer.XferAsset)
            || !AlgorandReceiptFields.Number(body, "amount", transfer.AssetAmount)
            || !AlgorandReceiptFields.String(body, "receiver", transfer.AssetReceiver.EncodeAsString())
            || body.TryGetProperty("close-to", out _) || tx.TryGetProperty("rekey-to", out _))
            return AZOAResult<ClientSigningObservation>.Failure("Indexed transaction evidence does not match the saved plan.");
        return AZOAResult<ClientSigningObservation>.Success(new(ClientSigningVerdicts.Confirmed, planned));
    }

    /// <summary>Decodes the saved plan, refusing any bytes the SDK would not re-encode identically.</summary>
    private Transaction PlannedClientTransaction(ClientSigningPlan plan)
    {
        if (plan.Version != 1 || plan.ChainType != ChainType || plan.Network != ActiveNetwork.ToString() || plan.Format != ClientSigningFormats.Algorand
            || plan.PredictedTransactionId is null || plan.ValidThroughHeight is null)
            throw new InvalidOperationException("The saved plan is not an Algorand client-signing plan for this network.");
        var unsigned = Convert.FromBase64String(plan.UnsignedPayloadBase64);
        var transaction = AlgorandEncoder.DecodeFromMsgPack<Transaction>(unsigned);
        if (transaction is not AssetTransferTransaction || transaction is AssetClawbackTransaction
            || !AlgorandEncoder.EncodeToMsgPackOrdered(transaction).AsSpan().SequenceEqual(unsigned)
            || transaction.Sender.EncodeAsString() != plan.Signer || transaction.TxID() != plan.PredictedTransactionId
            || transaction.LastValid != (ulong)plan.ValidThroughHeight.Value)
            throw new InvalidOperationException("The saved Algorand plan does not round-trip to its recorded identity.");
        return transaction;
    }
}
