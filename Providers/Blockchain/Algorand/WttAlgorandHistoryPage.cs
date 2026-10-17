using System.Text.Json;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Retains ordered application mutations and the exact target across fixed-boundary history pages.</summary>
internal static class WttAlgorandHistoryPage
{
    internal sealed record State(int Version, IReadOnlyList<JsonElement> RelevantRoots,
        ulong LastRound, ulong LastOffset, string? LastRootId, string? LastRootHash);

    internal static State Advance(ulong applicationId, string targetRoot, ulong targetRound, ulong targetOffset, State state,
        JsonElement transactions)
    {
        var relevant = state.RelevantRoots.ToList();
        var round = state.LastRound; var offset = state.LastOffset;
        var lastId = state.LastRootId; var lastHash = state.LastRootHash;
        foreach (var root in transactions.EnumerateArray())
        {
            var atRound = Unsigned(root, "confirmed-round"); var atOffset = Unsigned(root, "intra-round-offset");
            var id = Text(root, "id"); var hash = CanonicalJson.Hash(root);
            if (atRound == 0 || atRound > targetRound || atRound < round || atRound == round && atOffset < offset)
                throw new InvalidDataException("WTT indexed application history is not the fixed-boundary ascending query.");
            if (lastId is not null && atRound == round && atOffset == offset)
            {
                if (id != lastId || hash != lastHash) throw new InvalidDataException("Repeated WTT root evidence changed across query pages.");
                continue;
            }
            round = atRound; offset = atOffset; lastId = id; lastHash = hash;
            if (atRound == targetRound && atOffset > targetOffset) continue;
            var count = 0;
            if (id == targetRoot || ContainsMutation(root, 0)) relevant.Add(root.Clone());

            bool ContainsMutation(JsonElement transaction, int depth)
            {
                if (++count > 1024 || depth > 16 || transaction.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Malformed or oversized indexed WTT execution tree.");
                var found = false;
                if (transaction.TryGetProperty("created-application-index", out var created))
                {
                    if (created.ValueKind != JsonValueKind.Number || !created.TryGetUInt64(out var application))
                        throw new InvalidDataException("Malformed indexed application creation.");
                    found = application == applicationId;
                }
                if (transaction.TryGetProperty("application-transaction", out var call)
                    && Unsigned(call, "application-id") == applicationId)
                {
                    var completion = Text(call, "on-completion");
                    found |= completion is "update" or "delete";
                }
                if (!transaction.TryGetProperty("inner-txns", out var children)) return found;
                if (children.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Malformed indexed inner transaction tree.");
                foreach (var child in children.EnumerateArray()) found |= ContainsMutation(child, depth + 1);
                return found;
            }
        }
        return new(1, relevant, round, offset, lastId, lastHash);
    }

    private static ulong Unsigned(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(field, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetUInt64(out var result)
        ? result : throw new InvalidDataException("Indexed history requires an unsigned " + field + ".");

    private static string Text(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(field, out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(text.GetString())
        ? text.GetString()! : throw new InvalidDataException("Indexed history requires " + field + ".");
}
