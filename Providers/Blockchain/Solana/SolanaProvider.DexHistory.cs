using System.Text.Json;
using AZOA.WebAPI.Models.Dex;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record JupiterHistoryCursor(int Version, string PlanHash, string Genesis, ulong StartSlot,
        ulong UpperSlot, string UpperHash, ulong UpperHeight, ulong NextSlot, string ExpectedHash,
        ulong ExpectedHeight, ulong ScannedBlocks, string TranscriptHash, bool Complete);

    private async Task<(JupiterHistoryCursor? Cursor, PreparedSwapStage? Candidate)> ScanJupiterHistoryAsync(
        SwapIntent intent, SwapStagePlan plan, JupiterSavedPlan saved, JupiterCompilation compiled,
        SwapStageObservation? previous, CancellationToken ct)
    {
        var planHash = CanonicalJson.Hash(plan);
        JupiterHistoryCursor? cursor = null;
        if (previous?.EvidenceJson is { } evidence)
        {
            using var prior = JsonDocument.Parse(evidence);
            if (prior.RootElement.ValueKind == JsonValueKind.Object && prior.RootElement.TryGetProperty("history", out var history))
            {
                cursor = history.Deserialize<JupiterHistoryCursor>() ?? throw new InvalidDataException("Saved Jupiter history cursor is missing.");
                RequireJupiterCursor(cursor, planHash, saved.Recovery);
            }
        }
        if (cursor?.Complete == true) return (cursor, null);
        JsonElement? firstBlock = null;
        if (cursor is null)
        {
            var root = await DurableRpcAsync("getSlot", [new { commitment = "finalized" }], ct);
            if (root.ValueKind != JsonValueKind.Number || !root.TryGetUInt64(out var upper) || upper < (ulong)saved.Recovery.ContextSlot)
                throw new InvalidDataException("Finalized Jupiter history boundary precedes preparation.");
            var block = await ReadJupiterHistoryBlockAsync(upper, ct);
            var header = JupiterBlockHeader(block, upper);
            if (header.Height <= (ulong)saved.Recovery.LastValidBlockHeight) return (null, null);
            cursor = new(1, planHash, saved.Recovery.Genesis, (ulong)saved.Recovery.ContextSlot,
                upper, header.Hash, header.Height, upper, header.Hash, header.Height, 0,
                CanonicalJson.Hash(new { Domain = "jupiter-history/1", Plan = planHash, Slot = upper, header.Hash }), false);
            firstBlock = block;
        }
        for (var count = 0; count < 8 && !cursor.Complete; count++)
        {
            var block = firstBlock ?? await ReadJupiterHistoryBlockAsync(cursor.NextSlot, ct);
            firstBlock = null;
            var header = JupiterBlockHeader(block, cursor.NextSlot);
            if (header.Hash != cursor.ExpectedHash || header.Height != cursor.ExpectedHeight)
                throw new InvalidDataException("Finalized Jupiter block ancestry changed while scanning.");
            var transactions = block.GetProperty("transactions");
            if (transactions.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Complete finalized transaction bodies are required for Jupiter absence.");
            foreach (var transaction in transactions.EnumerateArray())
            {
                var encoded = transaction.GetProperty("transaction");
                if (encoded.ValueKind != JsonValueKind.Array || encoded.GetArrayLength() != 2 || encoded[1].GetString() != "base64" ||
                    encoded[0].GetString() is not { } text)
                    throw new InvalidDataException("Finalized block omitted canonical transaction wire.");
                var bytes = Convert.FromBase64String(text);
                if (bytes.Length is 0 or > 1232 || Convert.ToBase64String(bytes) != text || bytes[0] is 0 or > 127 || bytes.Length <= 1 + bytes[0] * 64)
                    throw new InvalidDataException("Finalized block transaction signature framing is invalid.");
                if (!bytes.AsSpan(1 + bytes[0] * 64).SequenceEqual(compiled.Message)) continue;
                var candidate = ValidateSignedSwapStage(intent, plan, JsonSerializer.Serialize(text));
                if (candidate.IsError || candidate.Result is null)
                    throw new InvalidDataException("Matching finalized Jupiter message lacks a valid original owner signature.");
                return (cursor, candidate.Result);
            }
            var completed = header.Parent < cursor.StartSlot;
            cursor = cursor with
            {
                NextSlot = header.Parent, ExpectedHash = header.PreviousHash,
                ExpectedHeight = header.Height - 1, ScannedBlocks = checked(cursor.ScannedBlocks + 1),
                TranscriptHash = CanonicalJson.Hash(new { Previous = cursor.TranscriptHash, Slot = cursor.NextSlot,
                    header.Hash, header.Parent, header.PreviousHash, Transactions = transactions.GetArrayLength() }),
                Complete = completed
            };
        }
        return (cursor, null);
    }

    private Task<JsonElement> ReadJupiterHistoryBlockAsync(ulong slot, CancellationToken ct)
        => DurableRpcAsync("getBlock", [slot, new { encoding = "base64", transactionDetails = "full", rewards = false,
            commitment = "finalized", maxSupportedTransactionVersion = 0 }], ct);

    private static (string Hash, string PreviousHash, ulong Parent, ulong Height) JupiterBlockHeader(JsonElement block, ulong slot)
    {
        if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("blockhash", out var hash) ||
            hash.ValueKind != JsonValueKind.String || hash.GetString() is not { Length: > 0 } identity ||
            !block.TryGetProperty("previousBlockhash", out var previous) || previous.ValueKind != JsonValueKind.String ||
            previous.GetString() is not { Length: > 0 } previousIdentity ||
            !block.TryGetProperty("parentSlot", out var parent) || parent.ValueKind != JsonValueKind.Number ||
            !parent.TryGetUInt64(out var parentSlot) || parentSlot >= slot ||
            !block.TryGetProperty("blockHeight", out var height) || height.ValueKind != JsonValueKind.Number ||
            !height.TryGetUInt64(out var blockHeight) || blockHeight == 0 || blockHeight > slot ||
            new PublicKey(identity).Key != identity || new PublicKey(previousIdentity).Key != previousIdentity)
            throw new InvalidDataException("Complete finalized block ancestry and height are required.");
        return (identity, previousIdentity, parentSlot, blockHeight);
    }

    private static void RequireJupiterCursor(JupiterHistoryCursor cursor, string planHash, DurableSolanaPlan recovery)
    {
        if (cursor.Version != 1 || cursor.PlanHash != planHash || cursor.Genesis != recovery.Genesis ||
            cursor.StartSlot != (ulong)recovery.ContextSlot || cursor.UpperSlot < cursor.StartSlot ||
            cursor.UpperHeight <= (ulong)recovery.LastValidBlockHeight || cursor.NextSlot > cursor.UpperSlot ||
            cursor.ScannedBlocks > cursor.UpperHeight || cursor.ExpectedHeight != cursor.UpperHeight - cursor.ScannedBlocks ||
            cursor.Complete != (cursor.NextSlot < cursor.StartSlot) || cursor.TranscriptHash is not { Length: 64 } ||
            string.IsNullOrEmpty(cursor.UpperHash) || string.IsNullOrEmpty(cursor.ExpectedHash))
            throw new InvalidDataException("Saved Jupiter history cursor belongs to different or inconsistent evidence.");
    }
}
