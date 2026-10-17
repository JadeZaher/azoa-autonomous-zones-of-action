using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using Algorand;
using Algorand.Algod.Model.Transactions;
using AlgorandEncoder = Algorand.Utils.Encoder;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider : IValueTransferTransactionModule
{
    private sealed record ValueTransferPlan(string UnsignedBase64, string ProtocolIdentity);

    /// <inheritdoc/>
    public async Task<AZOAResult<PreparedValueTransfer>> PrepareValueTransferAsync(ValueTransferIntent intent, CancellationToken ct)
    {
        if (IndexerHttpClient is null || string.IsNullOrWhiteSpace(intent.PreparationKey))
            return AZOAResult<PreparedValueTransfer>.Failure("An authoritative indexer and durable preparation key are required.");
        if (intent.ChainType != ChainType || intent.Network != ActiveNetwork
            || !ValidateAddressFormat(intent.Sender) || !ValidateAddressFormat(intent.Recipient)
            || (!intent.SigningContext.IsPlatform && (!intent.SigningContext.IsResolvableUserContext
                || intent.SigningContext.Scope != AzoaScopes.TransferSign))
            || !UInt64Text.TryParse(intent.AssetId, out var asset) || asset == 0
            || !UInt64Text.TryParse(intent.QuantityBaseUnits, out var amount) || amount == 0
            || (intent.Cost is not null && (!ValueCustodyIdentity.MatchesTransfer(intent)
                || !UInt64Text.TryParse(intent.Cost.MaximumNetworkFeeBaseUnits, out _))))
            return AZOAResult<PreparedValueTransfer>.Failure("Invalid value-transfer identity.");
        var identity = ValueTransferPreparationId(intent);
        var hash = CanonicalJson.Hash(intent);
        return await NativePreparationJournal.UseAsync(_custodyScopeFactory, async store =>
        {
            var row = await store.GetAsync(identity, ct);
            if (row is null)
            {
                var parameters = await ValidatedTransactionParametersAsync(ct);
                var transaction = new AssetTransferTransaction { Sender = new Address(intent.Sender),
                    AssetReceiver = new Address(intent.Recipient), XferAsset = asset, AssetAmount = amount,
                    Note = Encoding.UTF8.GetBytes(hash) };
                ApplySuggestedParams(transaction, parameters);
                transaction.Fee = AvmFee(transaction, parameters);
                transaction.Fee = AvmFee(transaction, parameters);
                if (intent.Cost is { } cost)
                {
                    var current = await QuoteValueCustodyAsync(cost.Quote.Request, ct);
                    if (current.IsError || current.Result is not { } quote || !quote.RecipientReady
                        || quote.ProtocolIdentity != cost.Quote.ProtocolIdentity
                        || quote.AssetAuthorityHash != cost.Quote.AssetAuthorityHash
                        || transaction.Fee!.Value > ulong.Parse(cost.MaximumNetworkFeeBaseUnits, System.Globalization.CultureInfo.InvariantCulture))
                        return AZOAResult<PreparedValueTransfer>.Failure("Current custody mechanics or native cost no longer match the admitted value budget.");
                }
                row = await store.ClaimAsync(new NativePreparation
                {
                    Id = identity, IntentHash = hash, Stage = "ValueTransfer", Generation = 0,
                    PlanJson = JsonSerializer.Serialize(new ValueTransferPlan(
                        Convert.ToBase64String(AlgorandEncoder.EncodeToMsgPackOrdered(transaction)), "algorand:" + parameters.GenesisHashB64)),
                    CreatedAt = DateTimeOffset.UtcNow,
                }, ct);
            }
            if (row.IntentHash != hash || row.Stage != "ValueTransfer" || row.Generation != 0)
                return AZOAResult<PreparedValueTransfer>.Failure("Preparation key belongs to a different value-transfer intent.");
            if (row.PreparedJson is not null)
            {
                var recovered = JsonSerializer.Deserialize<PreparedValueTransfer>(row.PreparedJson)!;
                var transaction = ValidatePreparedValueTransfer(recovered);
                var savedPlan = JsonSerializer.Deserialize<ValueTransferPlan>(row.PlanJson)!;
                if (CanonicalJson.Hash(recovered.Intent) != hash || recovered.ProtocolIdentity != savedPlan.ProtocolIdentity
                    || !AlgorandEncoder.EncodeToMsgPackOrdered(transaction).AsSpan().SequenceEqual(Convert.FromBase64String(savedPlan.UnsignedBase64)))
                    throw new InvalidOperationException("Recovered value transfer conflicts with its durable plan.");
                return AZOAResult<PreparedValueTransfer>.Success(recovered);
            }
            var plan = JsonSerializer.Deserialize<ValueTransferPlan>(row.PlanJson)
                ?? throw new InvalidOperationException("Value transfer preparation plan is absent.");
            var unsigned = Convert.FromBase64String(plan.UnsignedBase64);
            var signed = await SignViaCustodyAsync(unsigned, "prepare value transfer", intent.SigningContext);
            if (signed.IsError || signed.Result is null) return AZOAResult<PreparedValueTransfer>.Failure(signed.Message);
            var envelope = AlgorandSignedEnvelope.DecodeCanonical(Convert.ToBase64String(signed.Result));
            if (!AlgorandEncoder.EncodeToMsgPackOrdered(envelope.Tx).AsSpan().SequenceEqual(unsigned))
                throw new InvalidOperationException("Custody changed the saved value-transfer plan.");
            AlgorandSignedEnvelope.RequireSingleSender(envelope, intent.Sender);
            var prepared = new PreparedValueTransfer(envelope.Tx.TxID(), Convert.ToBase64String(signed.Result), plan.ProtocolIdentity, intent);
            ValidatePreparedValueTransfer(prepared);
            var saved = await store.SavePreparedAsync(identity, hash, JsonSerializer.Serialize(prepared), ct);
            return AZOAResult<PreparedValueTransfer>.Success(JsonSerializer.Deserialize<PreparedValueTransfer>(saved.PreparedJson!)!);
        });
    }

    /// <inheritdoc/>
    public async Task SubmitValueTransferAsync(PreparedValueTransfer prepared, CancellationToken ct)
    {
        var transaction = ValidatePreparedValueTransfer(prepared);
        if (string.IsNullOrWhiteSpace(prepared.Intent.PreparationKey))
            throw new InvalidOperationException("Legacy value transfers without preparation identity permit observation only.");
        var row = await NativePreparationJournal.UseAsync(_custodyScopeFactory,
            store => store.GetAsync(ValueTransferPreparationId(prepared.Intent), ct));
        if (row is null || row.Stage != "ValueTransfer" || row.IntentHash != CanonicalJson.Hash(prepared.Intent)
            || row.PreparedJson != JsonSerializer.Serialize(prepared))
            throw new InvalidOperationException("Value transfer is not the saved prepared candidate.");
        var plan = JsonSerializer.Deserialize<ValueTransferPlan>(row.PlanJson)!;
        if (plan.ProtocolIdentity != prepared.ProtocolIdentity
            || !AlgorandEncoder.EncodeToMsgPackOrdered(transaction).AsSpan().SequenceEqual(Convert.FromBase64String(plan.UnsignedBase64)))
            throw new InvalidOperationException("Value-transfer wire conflicts with its original plan.");
        await RequireValueTransferNetworkAsync(prepared, ct);
        var status = await AlgodHttpClient.GetFromJsonAsync<AlgodStatus>("/v2/status", ct)
            ?? throw new InvalidOperationException("Chain status unavailable.");
        if (status.LastRound < 0) throw new InvalidDataException("Algod returned an invalid current round.");
        if ((ulong)status.LastRound > transaction.LastValid)
            return;
        using var content = new ByteArrayContent(Convert.FromBase64String(prepared.SignedTransactionBase64));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-binary");
        using var response = await AlgodHttpClient.PostAsync("/v2/transactions", content, ct);
        response.EnsureSuccessStatusCode();
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!result.RootElement.TryGetProperty("txId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != prepared.TransactionId)
            throw new HttpRequestException("Algod returned a different value-transfer transaction identity.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ChainConfirmation>> ObserveValueTransferAsync(PreparedValueTransfer prepared, CancellationToken ct)
    {
        var transaction = ValidatePreparedValueTransfer(prepared);
        await RequireValueTransferNetworkAsync(prepared, ct);
        var unknown = AZOAResult<ChainConfirmation>.Success(ChainConfirmation.Unknown);
        if (IndexerHttpClient is not { } indexer) return unknown;
        using var response = await indexer.GetAsync($"/v2/transactions?txid={Uri.EscapeDataString(prepared.TransactionId)}", ct);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("current-round", out var round) || round.ValueKind != JsonValueKind.Number || !round.TryGetUInt64(out var indexedRound)
            || !root.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array)
            return unknown;
        if (root.TryGetProperty("next-token", out var continuation)
            && continuation.ValueKind != JsonValueKind.Null
            && (continuation.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(continuation.GetString()))) return unknown;
        var found = transactions.EnumerateArray().ToArray();
        if (found.Length > 1) return AZOAResult<ChainConfirmation>.Failure("Non-unique transaction evidence.");
        var evidenceRound = transaction.LastValid!.Value;
        if (found.Length == 1)
        {
            var tx = found[0];
            if (tx.ValueKind != JsonValueKind.Object
                || !tx.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != prepared.TransactionId
                || !tx.TryGetProperty("sender", out var sender) || sender.ValueKind != JsonValueKind.String || sender.GetString() != prepared.Intent.Sender
                || !tx.TryGetProperty("confirmed-round", out var confirmed) || confirmed.ValueKind != JsonValueKind.Number || !confirmed.TryGetUInt64(out evidenceRound)
                || evidenceRound < transaction.FirstValid || evidenceRound > transaction.LastValid
                || !tx.TryGetProperty("asset-transfer-transaction", out var transfer) || transfer.ValueKind != JsonValueKind.Object
                || !transfer.TryGetProperty("asset-id", out var asset) || asset.ValueKind != JsonValueKind.Number || asset.GetRawText() != prepared.Intent.AssetId
                || !transfer.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number || amount.GetRawText() != prepared.Intent.QuantityBaseUnits
                || !transfer.TryGetProperty("receiver", out var receiver) || receiver.ValueKind != JsonValueKind.String || receiver.GetString() != prepared.Intent.Recipient
                || transfer.TryGetProperty("sender", out _) || transfer.TryGetProperty("close-to", out _)
                || tx.TryGetProperty("rekey-to", out _) || tx.TryGetProperty("group", out _))
                return AZOAResult<ChainConfirmation>.Failure("Transaction evidence does not match the reserved value transfer.");
            if (!string.IsNullOrWhiteSpace(prepared.Intent.PreparationKey)
                && (!AlgorandReceiptFields.String(tx, "tx-type", "axfer") || !AlgorandReceiptFields.Number(tx, "fee", transaction.Fee!.Value)
                    || !AlgorandReceiptFields.Number(tx, "first-valid", transaction.FirstValid!.Value)
                    || !AlgorandReceiptFields.Number(tx, "last-valid", transaction.LastValid!.Value)
                    || !AlgorandReceiptFields.String(tx, "note", Convert.ToBase64String(transaction.Note))))
                return AZOAResult<ChainConfirmation>.Failure("Indexed transfer does not bind the saved value intent.");
        }
        if (evidenceRound == 0 || indexedRound < evidenceRound) return unknown;
        using var blockResponse = await indexer.GetAsync($"/v2/blocks/{evidenceRound}", ct);
        blockResponse.EnsureSuccessStatusCode();
        using var block = await JsonDocument.ParseAsync(await blockResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (block.RootElement.ValueKind != JsonValueKind.Object
            || !block.RootElement.TryGetProperty("genesis-hash", out var genesis) || genesis.ValueKind != JsonValueKind.String
            || prepared.ProtocolIdentity != "algorand:" + genesis.GetString())
            return AZOAResult<ChainConfirmation>.Failure("Indexed transaction evidence belongs to another genesis.");
        if (found.Length == 1) return AZOAResult<ChainConfirmation>.Success(ChainConfirmation.Confirmed);
        var status = await AlgodHttpClient.GetFromJsonAsync<AlgodStatus>("/v2/status", ct);
        return status is not null && status.LastRound >= 0 && (ulong)status.LastRound > transaction.LastValid
            ? AZOAResult<ChainConfirmation>.Success(ChainConfirmation.FailedOnChain,
                "Expired transaction is absent from the indexed finalized validity window.") : unknown;
    }

    private async Task RequireValueTransferNetworkAsync(PreparedValueTransfer prepared, CancellationToken ct)
    {
        var parameters = await ValidatedTransactionParametersAsync(ct);
        if (prepared.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
            throw new InvalidOperationException("Value transfer belongs to another chain genesis.");
    }

    private AssetTransferTransaction ValidatePreparedValueTransfer(PreparedValueTransfer prepared)
    {
        var signed = AlgorandSignedEnvelope.DecodeCanonical(prepared.SignedTransactionBase64);
        if (signed.Tx is AssetClawbackTransaction || signed.Tx is not AssetTransferTransaction transaction || prepared.Intent.ChainType != ChainType
            || prepared.Intent.Network != ActiveNetwork || transaction.TxID() != prepared.TransactionId
            || transaction.Sender.EncodeAsString() != prepared.Intent.Sender
            || transaction.AssetReceiver?.EncodeAsString() != prepared.Intent.Recipient
            || transaction.XferAsset.ToString() != prepared.Intent.AssetId
            || transaction.AssetAmount.ToString() != prepared.Intent.QuantityBaseUnits
            || transaction.AssetCloseTo is not null || transaction.RekeyTo is not null || transaction.Group is not null
            || transaction.GenesisHash is null || transaction.GenesisHash.Bytes.Length != 32
            || transaction.FirstValid is null || transaction.LastValid is null || transaction.FirstValid > transaction.LastValid
            || prepared.ProtocolIdentity != "algorand:" + Convert.ToBase64String(transaction.GenesisHash.Bytes))
            throw new InvalidOperationException("Prepared transfer does not match the authorized value intent.");
        if (!string.IsNullOrWhiteSpace(prepared.Intent.PreparationKey)
            && (transaction.Note is null || !transaction.Note.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(CanonicalJson.Hash(prepared.Intent)))))
            throw new InvalidOperationException("Value-transfer note does not commit the saved intent.");
        if (prepared.Intent.Cost is { } cost && (!ValueCustodyIdentity.MatchesTransfer(prepared.Intent)
            || prepared.ProtocolIdentity != cost.Quote.ProtocolIdentity || transaction.Fee is null
            || !UInt64Text.TryParse(cost.MaximumNetworkFeeBaseUnits, out var maximumFee) || transaction.Fee > maximumFee))
            throw new InvalidOperationException("Prepared value transfer exceeds its immutable accepted cost or protocol.");
        AlgorandSignedEnvelope.RequireSingleSender(signed, prepared.Intent.Sender);
        return transaction;
    }

    private static string ValueTransferPreparationId(ValueTransferIntent intent)
        => CanonicalJson.Hash(new { Domain = "algorand-value-transfer-v1", intent.ChainType, intent.Network, intent.PreparationKey });
}
