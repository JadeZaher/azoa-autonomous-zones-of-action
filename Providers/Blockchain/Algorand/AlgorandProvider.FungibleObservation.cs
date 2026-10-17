using System.Globalization;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc/>
    public async Task<AZOAResult<FungibleCreationObservation>> ObserveFungibleCreationAsync(PreparedFungibleCreation prepared, CancellationToken ct = default)
    {
        var tx = await ValidateFungiblePreparedAsync(prepared, ct);
        var parameters = await ValidatedTransactionParametersAsync(ct);
        if (prepared.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
            return AZOAResult<FungibleCreationObservation>.Failure("Creation observation belongs to another genesis.");
        var expired = (ulong)parameters.LastRound > tx.LastValid;
        var unresolved = new FungibleCreationObservation(expired ? "NeedsOperator" : "Pending", prepared.TransactionId, prepared.ProtocolIdentity);
        if (IndexerHttpClient is not { } indexer) return AZOAResult<FungibleCreationObservation>.Success(unresolved);
        using var response = await indexer.GetAsync($"/v2/transactions?txid={Uri.EscapeDataString(prepared.TransactionId)}", ct);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("current-round", out var current) || current.ValueKind != JsonValueKind.Number || !current.TryGetUInt64(out var indexedRound)
            || !root.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array
            || (root.TryGetProperty("next-token", out var continuation) && continuation.ValueKind != JsonValueKind.Null
                && (continuation.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(continuation.GetString()))))
            return AZOAResult<FungibleCreationObservation>.Success(unresolved);
        var candidates = transactions.EnumerateArray().ToArray();
        if (candidates.Length > 1) return AZOAResult<FungibleCreationObservation>.Failure("Creation evidence is not unique.");
        var evidenceRound = tx.LastValid!.Value;
        string? assetId = null;
        if (candidates.Length == 1)
        {
            var receipt = candidates[0];
            if (receipt.ValueKind != JsonValueKind.Object || !receipt.TryGetProperty("confirmed-round", out var round)
                || round.ValueKind != JsonValueKind.Number || !round.TryGetUInt64(out evidenceRound)
                || evidenceRound < tx.FirstValid || evidenceRound > tx.LastValid
                || !receipt.TryGetProperty("created-asset-index", out var created) || created.ValueKind != JsonValueKind.Number || !created.TryGetUInt64(out var asset) || asset == 0
                || !FungibleReceiptMatches(receipt, prepared, tx.Fee!.Value, tx.FirstValid!.Value, tx.LastValid.Value))
                return AZOAResult<FungibleCreationObservation>.Failure("Indexed creation does not match the saved transaction semantics.");
            assetId = asset.ToString(CultureInfo.InvariantCulture);
        }
        if (evidenceRound == 0 || indexedRound < evidenceRound) return AZOAResult<FungibleCreationObservation>.Success(unresolved);
        using var blockResponse = await indexer.GetAsync($"/v2/blocks/{evidenceRound}", ct);
        blockResponse.EnsureSuccessStatusCode();
        using var block = await JsonDocument.ParseAsync(await blockResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (block.RootElement.ValueKind != JsonValueKind.Object || !block.RootElement.TryGetProperty("genesis-hash", out var genesis)
            || genesis.ValueKind != JsonValueKind.String
            || prepared.ProtocolIdentity != "algorand:" + genesis.GetString())
            return AZOAResult<FungibleCreationObservation>.Failure("Indexer block belongs to another network.");
        if (assetId is null)
            return AZOAResult<FungibleCreationObservation>.Success(expired
                ? unresolved with { Verdict = "ExpiredAbsent", EvidenceJson = root.GetRawText() }
                : unresolved);
        return AZOAResult<FungibleCreationObservation>.Success(new("Confirmed", prepared.TransactionId,
            prepared.ProtocolIdentity, assetId, evidenceRound.ToString(CultureInfo.InvariantCulture), candidates[0].GetRawText()));
    }

    private static bool FungibleReceiptMatches(JsonElement receipt, PreparedFungibleCreation prepared, ulong fee, ulong first, ulong last)
    {
        var intent = prepared.Intent;
        if (!AlgorandReceiptFields.String(receipt, "id", prepared.TransactionId) || !AlgorandReceiptFields.String(receipt, "tx-type", "acfg")
            || !AlgorandReceiptFields.String(receipt, "sender", intent.CreatorAddress)
            || !AlgorandReceiptFields.Number(receipt, "fee", fee) || !AlgorandReceiptFields.Number(receipt, "first-valid", first) || !AlgorandReceiptFields.Number(receipt, "last-valid", last)
            || !AlgorandReceiptFields.String(receipt, "note", Convert.ToBase64String(Encoding.UTF8.GetBytes(FungibleCreationIdentity.IntentHash(intent))))
            || receipt.TryGetProperty("rekey-to", out _) || receipt.TryGetProperty("group", out _)
            || !receipt.TryGetProperty("asset-config-transaction", out var config) || config.ValueKind != JsonValueKind.Object
            || (config.TryGetProperty("asset-id", out var configured) && (configured.ValueKind != JsonValueKind.Number || !configured.TryGetUInt64(out var value) || value != 0))
            || !config.TryGetProperty("params", out var asset) || asset.ValueKind != JsonValueKind.Object)
            return false;
        return AlgorandReceiptFields.String(asset, "creator", intent.CreatorAddress)
            && AlgorandReceiptFields.Number(asset, "total", UInt64Text.Parse(intent.TotalBaseUnits))
            && AlgorandReceiptFields.Number(asset, "decimals", (ulong)intent.Decimals)
            && (asset.TryGetProperty("default-frozen", out var frozen)
                ? frozen.ValueKind is JsonValueKind.True or JsonValueKind.False && frozen.GetBoolean() == intent.DefaultFrozen : !intent.DefaultFrozen)
            && FungibleText(asset, "name", "name-b64", intent.Name)
            && FungibleText(asset, "unit-name", "unit-name-b64", intent.UnitName)
            && FungibleText(asset, "url", "url-b64", intent.Url ?? "")
            && FungibleOptionalString(asset, "metadata-hash", intent.MetadataHashBase64 ?? "")
            && FungibleOptionalString(asset, "manager", intent.ManagerAddress)
            && FungibleOptionalString(asset, "reserve", intent.ReserveAddress)
            && FungibleOptionalString(asset, "freeze", intent.FreezeAddress)
            && FungibleOptionalString(asset, "clawback", intent.ClawbackAddress);
    }

    private static bool FungibleOptionalString(JsonElement element, string field, string expected)
        => !element.TryGetProperty(field, out var value) ? expected.Length == 0
            : value.ValueKind == JsonValueKind.String && value.GetString() == expected;
    private static bool FungibleText(JsonElement element, string textField, string bytesField, string expected)
    {
        if (element.TryGetProperty(bytesField, out var bytes))
            return bytes.ValueKind == JsonValueKind.String && bytes.GetString() == Convert.ToBase64String(Encoding.UTF8.GetBytes(expected))
                && (!element.TryGetProperty(textField, out _) || AlgorandReceiptFields.String(element, textField, expected));
        return FungibleOptionalString(element, textField, expected);
    }
}
