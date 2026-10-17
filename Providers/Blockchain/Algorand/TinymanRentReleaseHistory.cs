using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Helpers;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

internal sealed record TinymanRentReleaseEvent(string TransactionId, ulong Round, ulong OuterOffset,
    string InnerPath, string BlockDigest);

/// <summary>Finds the first exact holding close in finalized block execution order after an attributed opt-in.</summary>
internal static class TinymanRentReleaseHistory
{
    internal static bool ScanBlock(string owner, ulong asset, string genesis, ulong allocationRound, ulong allocationOffset,
        ulong cleanupRound, ulong cleanupOffset, JsonElement block, TinymanRentReleaseEvent? prior,
        out TinymanRentReleaseEvent? earliest)
    {
        earliest = prior;
        if (asset == 0 || allocationRound > cleanupRound ||
            (allocationRound == cleanupRound && allocationOffset >= cleanupOffset) ||
            !TinymanSwapReceipt.Number(block, "round", out var round) || round < allocationRound || round > cleanupRound ||
            !TinymanSwapReceipt.Text(block, "genesis-hash", genesis) ||
            !block.TryGetProperty("transactions", out var roots) || roots.ValueKind != JsonValueKind.Array) return false;
        var blockDigest = CanonicalJson.Hash(block);
        ulong? previousOffset = null;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots.EnumerateArray())
        {
            if (!TinymanSwapReceipt.Number(root, "intra-round-offset", out var offset) ||
                (previousOffset is { } previous && offset <= previous) || !root.TryGetProperty("id", out var rootId) ||
                rootId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(rootId.GetString()) ||
                !identities.Add(rootId.GetString()!)) return false;
            previousOffset = offset;
            var stack = new Stack<(JsonElement Transaction, string Path)>();
            stack.Push((root, ""));
            while (stack.TryPop(out var item))
            {
                var tx = item.Transaction;
                if (tx.ValueKind != JsonValueKind.Object) return false;
                if (tx.TryGetProperty("confirmed-round", out var confirmed) &&
                    (confirmed.ValueKind != JsonValueKind.Number || !confirmed.TryGetUInt64(out var txRound) || txRound != round)) return false;
                var afterAllocation = round > allocationRound || offset > allocationOffset;
                var beforeCleanup = round < cleanupRound || offset < cleanupOffset || (offset == cleanupOffset && item.Path.Length == 0);
                if (afterAllocation && beforeCleanup && TinymanSwapReceipt.Text(tx, "sender", owner) &&
                    TinymanSwapReceipt.Text(tx, "tx-type", "axfer"))
                {
                    if (!tx.TryGetProperty("asset-transfer-transaction", out var transfer) ||
                        !TinymanSwapReceipt.Number(transfer, "asset-id", out var idAsset)) return false;
                    if (idAsset == asset && transfer.TryGetProperty("close-to", out var close))
                    {
                        if (close.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(close.GetString()) ||
                            transfer.TryGetProperty("sender", out _)) return false;
                        var candidate = new TinymanRentReleaseEvent(rootId.GetString()!, round, offset, item.Path, blockDigest);
                        if (earliest is null || Compare(candidate, earliest) < 0) earliest = candidate;
                        else if (Compare(candidate, earliest) == 0 && candidate != earliest) return false;
                    }
                }
                if (!tx.TryGetProperty("inner-txns", out var children)) continue;
                if (children.ValueKind != JsonValueKind.Array) return false;
                for (var index = children.GetArrayLength() - 1; index >= 0; index--)
                    stack.Push((children[index], item.Path + "/" + index.ToString("D10", CultureInfo.InvariantCulture)));
            }
        }
        return true;
    }

    internal static int Compare(TinymanRentReleaseEvent left, TinymanRentReleaseEvent right)
    {
        var round = left.Round.CompareTo(right.Round);
        if (round != 0) return round;
        var offset = left.OuterOffset.CompareTo(right.OuterOffset);
        return offset != 0 ? offset : StringComparer.Ordinal.Compare(left.InnerPath, right.InnerPath);
    }
}
