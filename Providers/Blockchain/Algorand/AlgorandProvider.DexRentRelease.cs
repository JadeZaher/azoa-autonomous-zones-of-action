using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    private sealed record TinymanReleaseCheckpoint(int Version, string Binding, long Pages, string? NextToken,
        string PageDigest, TinymanRentReleaseEvent? Earliest, bool Complete, IReadOnlyList<string> CursorHashes);

    /// <inheritdoc cref="IDexTransactionModule.ScanSwapRentReleaseAsync"/>
    public async Task<AZOAResult<SwapRentReleaseProgress>> ScanSwapRentReleaseAsync(SwapIntent intent,
        PreparedSwapStage allocation, PreparedSwapStage cleanup, string? checkpointJson, CancellationToken ct = default)
    {
        var allocationCheck = ValidateSignedSwapStage(intent, allocation.Plan, allocation.SignedPayloadJson);
        var cleanupCheck = ValidateSignedSwapStage(intent, cleanup.Plan, cleanup.SignedPayloadJson);
        if (allocationCheck.IsError || cleanupCheck.IsError || allocationCheck.Result is not { } original ||
            cleanupCheck.Result is not { } closing || !original.TransactionIds.SequenceEqual(allocation.TransactionIds) ||
            !closing.TransactionIds.SequenceEqual(cleanup.TransactionIds) || IndexerHttpClient is null)
            return AZOAResult<SwapRentReleaseProgress>.Failure("Rent release requires exact original signed stages and an indexer.");
        var allocatedWire = ReadTinymanPlan(intent, allocation.Plan);
        var cleanupWire = ReadTinymanPlan(intent, cleanup.Plan);
        if (allocatedWire.Stage != "OptIn" || cleanupWire.Stage != "CloseOptIn" || allocatedWire.Owner != cleanupWire.Owner ||
            allocatedWire.OutputAsset != cleanupWire.OutputAsset || allocatedWire.GenesisHash != cleanupWire.GenesisHash)
            return AZOAResult<SwapRentReleaseProgress>.Failure("Rent release stage identities differ.");
        var actual = await ResolveSwapStageAccountingAsync(intent, allocation, ct);
        if (actual.IsError || actual.Result is not { ActualLockedBalance: "100000" } attributed)
            return new() { Message = "Original incremental allocation is not proven; retain its reservation." };
        var allocated = await ReadTinymanIndexedTransactionAsync(allocation.TransactionIds[0], ct);
        var closed = await ReadTinymanIndexedTransactionAsync(cleanup.TransactionIds[0], ct);
        if (allocated?.Transaction is not { } allocatedTx || closed?.Transaction is not { } closedTx ||
            !TinymanSwapReceipt.Number(allocatedTx, "confirmed-round", out var firstRound) ||
            !TinymanSwapReceipt.Number(allocatedTx, "intra-round-offset", out var firstOffset) ||
            !TinymanSwapReceipt.Number(closedTx, "confirmed-round", out var lastRound) ||
            !TinymanSwapReceipt.Number(closedTx, "intra-round-offset", out var lastOffset) ||
            firstRound > lastRound || (firstRound == lastRound && firstOffset >= lastOffset) ||
            allocated.Value.IndexedRound < lastRound || closed.Value.IndexedRound < lastRound)
            return new() { Message = "Finalized allocation and cleanup positions are not available." };
        var finalBlock = await ReadTinymanIndexerJsonAsync($"/v2/blocks/{lastRound.ToString(CultureInfo.InvariantCulture)}", ct);
        if (finalBlock is null || !TinymanSwapReceipt.Number(finalBlock.Value, "round", out var finalRound) || finalRound != lastRound ||
            !TinymanSwapReceipt.TryRead(cleanupWire, cleanup.TransactionIds, finalBlock.Value, out _))
            return new() { Message = "The exact finalized zero-holding cleanup is not proven." };
        var firstBlock = firstRound == lastRound ? finalBlock :
            await ReadTinymanIndexerJsonAsync($"/v2/blocks/{firstRound.ToString(CultureInfo.InvariantCulture)}", ct);
        if (firstBlock is null || !TinymanSwapReceipt.TryRead(allocatedWire, allocation.TransactionIds, firstBlock.Value, out _) ||
            !TinymanReleasePosition(firstBlock.Value, allocation.TransactionIds[0], firstRound, firstOffset) ||
            !TinymanReleasePosition(finalBlock.Value, cleanup.TransactionIds[0], lastRound, lastOffset))
            return new() { Message = "Indexed allocation or cleanup position differs from its finalized block." };
        var binding = CanonicalJson.Hash(new { intent, allocation, cleanup, firstRound, firstOffset, lastRound, lastOffset,
            allocationEvidence = attributed.EvidenceJson, cleanupBlockDigest = CanonicalJson.Hash(finalBlock.Value) });
        var checkpoint = checkpointJson is null ? new TinymanReleaseCheckpoint(1, binding, 0, null, binding, null, false, [])
            : JsonSerializer.Deserialize<TinymanReleaseCheckpoint>(checkpointJson);
        if (checkpoint is null || checkpoint.Version != 1 || checkpoint.Binding != binding || checkpoint.Pages < 0 ||
            checkpoint.PageDigest is not { Length: 64 } || checkpoint.Complete || checkpoint.CursorHashes is null ||
            checkpoint.CursorHashes.Count != checkpoint.Pages || checkpoint.CursorHashes.Any(hash => hash is not { Length: 64 }) ||
            checkpoint.CursorHashes.Distinct(StringComparer.Ordinal).Count() != checkpoint.CursorHashes.Count ||
            (checkpoint.Pages == 0 ? checkpoint.NextToken is not null : string.IsNullOrWhiteSpace(checkpoint.NextToken)))
            return AZOAResult<SwapRentReleaseProgress>.Failure("Saved rent history checkpoint differs from its finalized boundaries.");
        var path = "/v2/transactions?address=" + Uri.EscapeDataString(allocatedWire.Owner) +
            "&asset-id=" + allocatedWire.OutputAsset.ToString(CultureInfo.InvariantCulture) + "&min-round=" + firstRound.ToString(CultureInfo.InvariantCulture) +
            "&max-round=" + lastRound.ToString(CultureInfo.InvariantCulture) + "&limit=25";
        if (checkpoint.NextToken is { } token) path += "&next=" + Uri.EscapeDataString(token);
        var page = await ReadTinymanIndexerJsonAsync(path, ct);
        if (page is null || !TinymanSwapReceipt.Number(page.Value, "current-round", out var currentRound) || currentRound < lastRound ||
            !page.Value.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array || transactions.GetArrayLength() > 25)
            return new() { Message = "A complete caught-up rent history page is not available." };
        string? next = null;
        if (page.Value.TryGetProperty("next-token", out var nextValue) && nextValue.ValueKind != JsonValueKind.Null)
        {
            if (nextValue.ValueKind != JsonValueKind.String) return AZOAResult<SwapRentReleaseProgress>.Failure("Invalid rent history cursor.");
            next = nextValue.GetString();
            if (next?.Length == 0) next = null;
            if (next is not null && (next.Length > 4096 || string.IsNullOrWhiteSpace(next) ||
                checkpoint.CursorHashes.Contains(CanonicalJson.Hash(new { cursor = next }), StringComparer.Ordinal)))
                return AZOAResult<SwapRentReleaseProgress>.Failure("Rent history cursor did not advance.");
        }
        var earliest = checkpoint.Earliest;
        var blocks = new Dictionary<ulong, JsonElement>();
        foreach (var transaction in transactions.EnumerateArray())
        {
            if (!TinymanSwapReceipt.Number(transaction, "confirmed-round", out var round) || round < firstRound || round > lastRound ||
                !transaction.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                return AZOAResult<SwapRentReleaseProgress>.Failure("Rent history contains an invalid root transaction.");
            if (!blocks.TryGetValue(round, out var block))
            {
                var fetched = await ReadTinymanIndexerJsonAsync($"/v2/blocks/{round.ToString(CultureInfo.InvariantCulture)}", ct);
                if (fetched is null) return new() { Message = "A finalized history block is unavailable; retry the same page." };
                block = fetched.Value;
                if (!TinymanRentReleaseHistory.ScanBlock(allocatedWire.Owner, allocatedWire.OutputAsset, allocatedWire.GenesisHash,
                    firstRound, firstOffset, lastRound, lastOffset, block, earliest, out earliest))
                    return AZOAResult<SwapRentReleaseProgress>.Failure("Finalized rent history order or chain identity differs.");
                blocks.Add(round, block);
            }
            if (!block.GetProperty("transactions").EnumerateArray().Any(root => TinymanSwapReceipt.Text(root, "id", id.GetString()!)))
                return AZOAResult<SwapRentReleaseProgress>.Failure("Indexed history root is absent from its finalized block.");
        }
        var updated = new TinymanReleaseCheckpoint(1, binding, checked(checkpoint.Pages + 1), next,
            CanonicalJson.Hash(new { previous = checkpoint.PageDigest, page = page.Value }), earliest, next is null,
            next is null ? checkpoint.CursorHashes : checkpoint.CursorHashes.Append(CanonicalJson.Hash(new { cursor = next })).ToArray());
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(updated));
        if (!updated.Complete) return AZOAResult<SwapRentReleaseProgress>.Success(new(json, false));
        if (earliest is null) return AZOAResult<SwapRentReleaseProgress>.Failure("Exhausted history omitted the proven holding close.");
        var cleanupAttributed = earliest.TransactionId == cleanup.TransactionIds[0] && earliest.Round == lastRound &&
            earliest.OuterOffset == lastOffset && earliest.InnerPath.Length == 0;
        var evidence = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            format = "tinyman-holding-release/1", binding, allocationTransactionId = allocation.TransactionIds[0],
            cleanupTransactionId = cleanup.TransactionIds[0], releasedBy = cleanupAttributed ? "Cleanup" : "EarlierExternalClose",
            release = earliest, exhaustedPageDigest = updated.PageDigest, pages = updated.Pages,
            actualReleased = attributed.ActualLockedBalance,
        }));
        return AZOAResult<SwapRentReleaseProgress>.Success(new(json, true, attributed.ActualLockedBalance, evidence));
    }

    private static bool TinymanReleasePosition(JsonElement block, string id, ulong round, ulong offset)
    {
        if (!TinymanSwapReceipt.Number(block, "round", out var actualRound) || actualRound != round ||
            !block.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array) return false;
        var exact = transactions.EnumerateArray().Where(tx => TinymanSwapReceipt.Text(tx, "id", id)).ToArray();
        return exact.Length == 1 && TinymanSwapReceipt.Number(exact[0], "intra-round-offset", out var actualOffset) && actualOffset == offset;
    }
}
