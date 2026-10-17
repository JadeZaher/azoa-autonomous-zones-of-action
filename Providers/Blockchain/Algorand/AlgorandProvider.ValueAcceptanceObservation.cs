using System.Net.Http.Json;
using System.Text.Json;
using Algorand.Algod.Model;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc/>
    public async Task<AZOAResult<ChainConfirmation>> ObserveValueAcceptanceAsync(PreparedValueAcceptance prepared, CancellationToken ct)
    {
        var transaction = ValidatePreparedValueAcceptance(prepared);
        var parameters = await ValidatedTransactionParametersAsync(ct);
        if (prepared.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
            return AZOAResult<ChainConfirmation>.Failure("Acceptance observation belongs to another physical network.");
        var unknown = AZOAResult<ChainConfirmation>.Success(ChainConfirmation.Unknown);
        if (IndexerHttpClient is not { } indexer) return unknown;
        using var response = await indexer.GetAsync($"/v2/transactions?txid={Uri.EscapeDataString(prepared.TransactionId)}", ct);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        if (!ValueUnsigned(root, "current-round", out var indexedRound)
            || !root.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array
            || (root.TryGetProperty("next-token", out var continuation) && continuation.ValueKind != JsonValueKind.Null
                && (continuation.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(continuation.GetString())))) return unknown;
        var found = transactions.EnumerateArray().ToArray();
        if (found.Length > 1) return AZOAResult<ChainConfirmation>.Failure("Acceptance transaction evidence is not unique.");
        var evidenceRound = transaction.LastValid!.Value;
        if (found.Length == 1)
        {
            var tx = found[0];
            if (!AlgorandReceiptFields.String(tx, "id", prepared.TransactionId)
                || !AlgorandReceiptFields.String(tx, "sender", prepared.Intent.Recipient)
                || !AlgorandReceiptFields.String(tx, "tx-type", "axfer")
                || !ValueUnsigned(tx, "confirmed-round", out evidenceRound)
                || evidenceRound < transaction.FirstValid || evidenceRound > transaction.LastValid
                || !AlgorandReceiptFields.Number(tx, "fee", transaction.Fee!.Value)
                || !AlgorandReceiptFields.Number(tx, "first-valid", transaction.FirstValid!.Value)
                || !AlgorandReceiptFields.Number(tx, "last-valid", transaction.LastValid!.Value)
                || !AlgorandReceiptFields.String(tx, "note", Convert.ToBase64String(transaction.Note))
                || tx.TryGetProperty("rekey-to", out _) || tx.TryGetProperty("group", out _)
                || !tx.TryGetProperty("asset-transfer-transaction", out var transfer) || transfer.ValueKind != JsonValueKind.Object
                || !AlgorandReceiptFields.Number(transfer, "asset-id", transaction.XferAsset)
                || !AlgorandReceiptFields.String(transfer, "receiver", prepared.Intent.Recipient)
                || (transfer.TryGetProperty("amount", out _) && !AlgorandReceiptFields.Number(transfer, "amount", 0))
                || transfer.TryGetProperty("sender", out _) || transfer.TryGetProperty("close-to", out _))
                return AZOAResult<ChainConfirmation>.Failure("Indexed acceptance does not match the exact saved recipient operation.");
        }
        if (evidenceRound == 0 || indexedRound < evidenceRound) return unknown;
        using var blockResponse = await indexer.GetAsync($"/v2/blocks/{evidenceRound}", ct);
        blockResponse.EnsureSuccessStatusCode();
        using var block = await JsonDocument.ParseAsync(await blockResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (block.RootElement.ValueKind != JsonValueKind.Object
            || !block.RootElement.TryGetProperty("genesis-hash", out var genesis) || genesis.ValueKind != JsonValueKind.String
            || prepared.ProtocolIdentity != "algorand:" + genesis.GetString())
            return AZOAResult<ChainConfirmation>.Failure("Indexed acceptance evidence belongs to another genesis.");
        if (found.Length == 1) return AZOAResult<ChainConfirmation>.Success(ChainConfirmation.Confirmed);
        var status = await AlgodHttpClient.GetFromJsonAsync<AlgodStatus>("/v2/status", ct);
        return status is not null && status.LastRound >= 0 && (ulong)status.LastRound > transaction.LastValid
            ? AZOAResult<ChainConfirmation>.Success(ChainConfirmation.FailedOnChain,
                "Expired acceptance is absent from the complete indexed validity window.") : unknown;
    }
}
