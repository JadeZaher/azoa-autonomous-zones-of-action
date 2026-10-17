using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc/>
    public async Task<AZOAResult<NativeStageObservation>> ObserveNativeStageAsync(NativeOperationIntent intent, NativeStagePreparation prepared, CancellationToken ct = default)
    {
        var transaction = await ValidateAvmPreparedAsync(intent, prepared, ct);
        var parameters = await AlgodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct);
        if (parameters is null || prepared.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
            return Error<NativeStageObservation>("AVM observation belongs to another genesis.");
        using var pending = await AlgodHttpClient.GetAsync($"/v2/transactions/pending/{prepared.TransactionId}", ct);
        if (pending.StatusCode != HttpStatusCode.NotFound)
        {
            pending.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await pending.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            if (root.TryGetProperty("confirmed-round", out var confirmed) && confirmed.TryGetUInt64(out var round) && round > 0)
            {
                if (!root.TryGetProperty("txn", out var returned)) return Error<NativeStageObservation>("Algod confirmation lacks signed transaction evidence.");
                var decoded = Encoder.DecodeFromJson<SignedTransaction>(returned.GetRawText());
                if (!Encoder.EncodeToMsgPackOrdered(decoded).AsSpan().SequenceEqual(Convert.FromBase64String(prepared.SignedTransactionBase64))
                    || round < transaction.FirstValid || round > transaction.LastValid)
                    return Error<NativeStageObservation>("Algod confirmation differs from the saved AVM wire.");
                using var blockResponse = await AlgodHttpClient.GetAsync($"/v2/blocks/{round}?format=json", ct);
                blockResponse.EnsureSuccessStatusCode();
                using var block = await JsonDocument.ParseAsync(await blockResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (!block.RootElement.TryGetProperty("block", out var header) || !header.TryGetProperty("gh", out var genesis)
                    || prepared.ProtocolIdentity != "algorand:" + genesis.GetString())
                    return Error<NativeStageObservation>("AVM confirmed block genesis is not proven.");
                var input = intent.Input.Deserialize<AvmNativeInput>(AvmJson)!;
                string? resource = input.ApplicationId;
                if (input.Operation == "Create")
                {
                    if (!root.TryGetProperty("application-index", out var app) || !app.TryGetUInt64(out var id) || id == 0)
                        return Error<NativeStageObservation>("Confirmed AVM creation has no application identifier.");
                    resource = id.ToString(CultureInfo.InvariantCulture);
                }
                else if (!await ProveAvmCallProgramsAsync(input, prepared.TransactionId, round, ct))
                    return Ok(new NativeStageObservation("Unknown", prepared.TransactionId, prepared.ProtocolIdentity,
                        Detail: "Application program history at this call position is incomplete or differs from the admitted artifact."));
                return Ok(new NativeStageObservation("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity, resource,
                    JsonSerializer.Serialize(new { transactionId = prepared.TransactionId, round, applicationId = resource,
                        artifactHash = intent.ArtifactHash, compiledProgramsHash = CanonicalJson.Hash(input.Artifact) }),
                    Accounting: new(transaction.Fee!.Value.ToString(CultureInfo.InvariantCulture), [])));
            }
        }
        if ((ulong)parameters.LastRound <= transaction.LastValid)
            return Ok(new NativeStageObservation("Pending", prepared.TransactionId, prepared.ProtocolIdentity));
        var indexer = IndexerHttpClient;
        if (indexer is null) return Ok(new NativeStageObservation("Unknown", prepared.TransactionId, prepared.ProtocolIdentity,
            Detail: "Expired validity window requires indexed finalized absence evidence."));
        using var response = await indexer.GetAsync($"/v2/transactions?txid={Uri.EscapeDataString(prepared.TransactionId)}", ct);
        response.EnsureSuccessStatusCode();
        using var indexed = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var found = indexed.RootElement;
        if (found.TryGetProperty("next-token", out var continuation) && continuation.ValueKind != JsonValueKind.Null
                && (continuation.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(continuation.GetString()))
            || !found.TryGetProperty("current-round", out var current) || !current.TryGetUInt64(out var indexedRound)
            || !found.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array
            || transactions.GetArrayLength() > 1)
            return Ok(new NativeStageObservation("Unknown", prepared.TransactionId, prepared.ProtocolIdentity,
                Detail: "Preserve the original wire until complete authoritative transaction history is available."));
        if (transactions.GetArrayLength() == 1)
            return await ObserveIndexedAvmReceiptAsync(intent, prepared, transaction, transactions[0], indexedRound, ct);
        if (indexedRound < transaction.LastValid)
            return Ok(new NativeStageObservation("Unknown", prepared.TransactionId, prepared.ProtocolIdentity));
        using var endResponse = await indexer.GetAsync($"/v2/blocks/{transaction.LastValid}", ct);
        endResponse.EnsureSuccessStatusCode();
        using var end = await JsonDocument.ParseAsync(await endResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return end.RootElement.TryGetProperty("genesis-hash", out var endGenesis) && prepared.ProtocolIdentity == "algorand:" + endGenesis.GetString()
            ? Ok(new NativeStageObservation("ExpiredNoEffect", prepared.TransactionId, prepared.ProtocolIdentity))
            : Error<NativeStageObservation>("Indexed AVM absence evidence belongs to another genesis.");
    }

    private async Task<AZOAResult<NativeStageObservation>> ObserveIndexedAvmReceiptAsync(NativeOperationIntent intent,
        NativeStagePreparation prepared, Transaction transaction, JsonElement receipt, ulong indexedRound, CancellationToken ct)
    {
        var input = intent.Input.Deserialize<AvmNativeInput>(AvmJson)!;
        if (!receipt.TryGetProperty("id", out var id) || id.GetString() != prepared.TransactionId
            || !receipt.TryGetProperty("sender", out var sender) || sender.GetString() != input.Sender
            || !receipt.TryGetProperty("tx-type", out var type) || type.GetString() != "appl"
            || !receipt.TryGetProperty("note", out var note) || note.GetString() != Convert.ToBase64String(transaction.Note)
            || !receipt.TryGetProperty("confirmed-round", out var confirmation) || !confirmation.TryGetUInt64(out var round)
            || round < transaction.FirstValid || round > transaction.LastValid || indexedRound < round
            || !receipt.TryGetProperty("fee", out var fee) || !fee.TryGetUInt64(out var paidFee) || paidFee != transaction.Fee
            || !receipt.TryGetProperty("application-transaction", out var call)
            || !call.TryGetProperty("application-id", out var appId) || !appId.TryGetUInt64(out var app)
            || app != (input.Operation == "Create" ? 0 : ulong.Parse(input.ApplicationId!, CultureInfo.InvariantCulture))
            || receipt.TryGetProperty("rekey-to", out _) || receipt.TryGetProperty("group", out _))
            return Error<NativeStageObservation>("Indexed receipt does not prove the admitted AVM transaction.");
        string? resource = input.ApplicationId;
        if (input.Operation == "Create")
        {
            if (!receipt.TryGetProperty("created-application-index", out var created) || !created.TryGetUInt64(out var createdId) || createdId == 0)
                return Error<NativeStageObservation>("Indexed AVM creation lacks its application identifier.");
            resource = createdId.ToString(CultureInfo.InvariantCulture);
        }
        using var response = await IndexerHttpClient!.GetAsync($"/v2/blocks/{round}", ct);
        response.EnsureSuccessStatusCode();
        using var block = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!block.RootElement.TryGetProperty("genesis-hash", out var genesis) || prepared.ProtocolIdentity != "algorand:" + genesis.GetString())
            return Error<NativeStageObservation>("Indexed AVM confirmation belongs to another genesis.");
        if (input.Operation == "Call" && !await ProveAvmCallProgramsAsync(input, prepared.TransactionId, round, ct))
            return Ok(new NativeStageObservation("Unknown", prepared.TransactionId, prepared.ProtocolIdentity,
                Detail: "Application program history at this call position is incomplete or differs from the admitted artifact."));
        return Ok(new NativeStageObservation("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity, resource,
            JsonSerializer.Serialize(new { transactionId = prepared.TransactionId, round, applicationId = resource,
                artifactHash = intent.ArtifactHash, compiledProgramsHash = CanonicalJson.Hash(input.Artifact) }),
            Accounting: new(transaction.Fee!.Value.ToString(CultureInfo.InvariantCulture), [])));
    }
}
