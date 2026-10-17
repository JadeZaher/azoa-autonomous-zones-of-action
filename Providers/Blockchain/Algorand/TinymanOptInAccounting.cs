using System.Globalization;
using System.Text.Json;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Reconstructs output holding presence immediately before the exact opt-in within its finalized block.</summary>
internal static class TinymanOptInAccounting
{
    internal static bool TryResolve(TinymanWireParameters wire, string transactionId, JsonElement block,
        JsonElement accountResponse, out string? actualRent, out string? evidenceJson)
    {
        actualRent = null;
        evidenceJson = null;
        if (wire.Stage != "OptIn" || wire.OutputAsset == 0 || accountResponse.ValueKind != JsonValueKind.Object ||
            !TinymanSwapReceipt.TryRead(wire, [transactionId], block, out _) ||
            !TinymanSwapReceipt.Number(block, "round", out var round) || round < 2 ||
            !TinymanSwapReceipt.Text(block, "genesis-hash", wire.GenesisHash) ||
            !accountResponse.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object ||
            !TinymanSwapReceipt.Text(account, "address", wire.Owner) ||
            !TinymanSwapReceipt.Number(account, "round", out var accountRound) || accountRound != round - 1 ||
            !block.TryGetProperty("transactions", out var topLevel) || topLevel.ValueKind != JsonValueKind.Array ||
            !Presence(account, wire.OutputAsset, out var present)) return false;
        var outer = topLevel.EnumerateArray().ToArray();
        var prior = new List<JsonElement>();
        ulong? target = null;
        ulong? previous = null;
        foreach (var tx in outer)
        {
            if (!TinymanSwapReceipt.Number(tx, "intra-round-offset", out var offset) ||
                previous is { } last && offset <= last) return false;
            previous = offset;
            if (TinymanSwapReceipt.Text(tx, "id", transactionId))
            {
                if (target is not null) return false;
                target = offset;
            }
            else if (target is null) prior.Add(tx);
        }
        if (target is null) return false;
        var stack = new Stack<JsonElement>(prior.AsEnumerable().Reverse());
        while (stack.TryPop(out var tx))
        {
            if (tx.ValueKind != JsonValueKind.Object) return false;
            if (tx.TryGetProperty("inner-txns", out var inner))
            {
                if (inner.ValueKind != JsonValueKind.Array) return false;
                foreach (var child in inner.EnumerateArray().Reverse()) stack.Push(child);
            }
            if (TinymanSwapReceipt.Text(tx, "tx-type", "axfer"))
            {
                if (!tx.TryGetProperty("asset-transfer-transaction", out var transfer) ||
                    !TinymanSwapReceipt.Number(transfer, "asset-id", out var asset)) return false;
                if (asset != wire.OutputAsset) continue;
                if (!TinymanSwapReceipt.Number(transfer, "amount", out var amount) ||
                    !transfer.TryGetProperty("receiver", out var receiver) || receiver.ValueKind != JsonValueKind.String ||
                    !tx.TryGetProperty("sender", out var sender) || sender.ValueKind != JsonValueKind.String) return false;
                var clawback = transfer.TryGetProperty("sender", out var effective);
                if (clawback && effective.ValueKind != JsonValueKind.String) return false;
                if (!clawback && sender.GetString() == wire.Owner && receiver.GetString() == wire.Owner && amount == 0)
                    present = true;
                if (transfer.TryGetProperty("close-to", out var close))
                {
                    if (close.ValueKind != JsonValueKind.String) return false;
                    if ((clawback ? effective.GetString() : sender.GetString()) == wire.Owner) present = false;
                }
            }
            else if (TinymanSwapReceipt.Text(tx, "tx-type", "acfg"))
            {
                if (TinymanSwapReceipt.Number(tx, "created-asset-index", out var created) && created == wire.OutputAsset)
                {
                    if (!TinymanSwapReceipt.Text(tx, "sender", wire.Owner)) continue;
                    present = true;
                }
                else if (tx.TryGetProperty("asset-config-transaction", out var config) &&
                    TinymanSwapReceipt.Number(config, "asset-id", out var configured) && configured == wire.OutputAsset &&
                    !config.TryGetProperty("params", out _)) present = false;
            }
        }
        actualRent = present ? "0" : "100000";
        evidenceJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            format = "algorand-opt-in-ordered-rent/1", transactionId, genesisHash = wire.GenesisHash,
            round = round.ToString(CultureInfo.InvariantCulture), offset = target.Value.ToString(CultureInfo.InvariantCulture),
            priorAccountRound = accountRound.ToString(CultureInfo.InvariantCulture), presentBefore = present,
            actualLockedBalance = actualRent, blockDigest = CanonicalJson.Hash(block), accountDigest = CanonicalJson.Hash(accountResponse),
        }));
        return true;
    }

    private static bool Presence(JsonElement account, ulong asset, out bool present)
    {
        present = false;
        if (!account.TryGetProperty("assets", out var assets))
            return TinymanSwapReceipt.Number(account, "total-assets-opted-in", out var count) && count == 0;
        if (assets.ValueKind != JsonValueKind.Array) return false;
        var seen = new HashSet<ulong>();
        foreach (var holding in assets.EnumerateArray())
        {
            if (!TinymanSwapReceipt.Number(holding, "asset-id", out var id) || !seen.Add(id)) return false;
            if (id != asset) continue;
            if (holding.TryGetProperty("deleted", out var deleted))
            {
                if (deleted.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                present = !deleted.GetBoolean();
            }
            else present = true;
        }
        return true;
    }
}
