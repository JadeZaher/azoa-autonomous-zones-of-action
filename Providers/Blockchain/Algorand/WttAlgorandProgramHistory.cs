using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Replays complete indexed application mutations at an exact root transaction and inner execution path.</summary>
internal static class WttAlgorandProgramHistory
{
    internal static bool Matches(IReadOnlyList<JsonElement> roots, ulong applicationId, string rootId, string targetPath,
        ulong targetRound, ulong targetOffset, string approvalHash, string clearHash)
    {
        try
        {
            var ordered = new SortedDictionary<(ulong Round, ulong Offset), JsonElement>();
            foreach (var root in roots)
            {
                var round = Unsigned(root, "confirmed-round"); var offset = Unsigned(root, "intra-round-offset");
                if (round > targetRound || round == targetRound && offset > targetOffset) continue;
                var key = (round, offset);
                if (ordered.TryGetValue(key, out var previous) && CanonicalJson.Hash(previous) != CanonicalJson.Hash(root)) return false;
                ordered[key] = root;
            }
            var created = false; var active = false; var found = false; var matched = false;
            string? approval = null, clear = null;
            foreach (var (position, root) in ordered)
            {
                var targetRoot = position == (targetRound, targetOffset) && Text(root, "id") == rootId;
                if (position == (targetRound, targetOffset) && !targetRoot) return false;
                var count = 0;
                if (!Visit(root, "", 0)) return false;
                if (found) return matched;

                bool Visit(JsonElement transaction, string path, int depth)
                {
                    if (++count > 1024 || depth > 16 || transaction.ValueKind != JsonValueKind.Object) return false;
                    JsonElement app = default;
                    var isApp = transaction.TryGetProperty("application-transaction", out app);
                    var called = isApp ? Unsigned(app, "application-id") : 0;
                    if (targetRoot && path == targetPath)
                    {
                        if (found || !isApp || called != applicationId) return false;
                        found = true; matched = created && active && approval == approvalHash && clear == clearHash;
                        return true;
                    }
                    if (transaction.TryGetProperty("inner-txns", out var children))
                    {
                        if (children.ValueKind != JsonValueKind.Array) return false;
                        var index = 0;
                        foreach (var child in children.EnumerateArray())
                        {
                            var childPath = path.Length == 0 ? index.ToString(CultureInfo.InvariantCulture) : path + "/" + index.ToString(CultureInfo.InvariantCulture);
                            index++;
                            if (!Visit(child, childPath, depth + 1)) return false;
                            if (found) return true;
                        }
                    }
                    var creates = transaction.TryGetProperty("created-application-index", out var id)
                        && id.ValueKind == JsonValueKind.Number && id.TryGetUInt64(out var createdId) && createdId == applicationId;
                    if (!creates && called != applicationId) return true;
                    if (!isApp) return false;
                    var completion = Text(app, "on-completion");
                    if (creates)
                    {
                        if (created || called != 0 || completion != "noop") return false;
                        created = true; active = true;
                    }
                    else if (completion == "delete") { active = false; return true; }
                    else if (completion != "update") return true;
                    if (!created || !active) return false;
                    approval = ProgramHash(Text(app, "approval-program"));
                    clear = ProgramHash(Text(app, "clear-state-program"));
                    return true;
                }
            }
            return false;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException)
        { return false; }
    }

    private static string ProgramHash(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length == 0 || Convert.ToBase64String(bytes) != encoded) throw new InvalidDataException("Noncanonical application program.");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static ulong Unsigned(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.Number && field.TryGetUInt64(out var number)
            ? number : throw new InvalidDataException("Missing indexed application position.");

    private static string Text(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()! : throw new InvalidDataException("Missing indexed application identity.");
}
