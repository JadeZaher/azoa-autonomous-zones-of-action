using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    private async Task<bool> ProveAvmCallProgramsAsync(AvmNativeInput input, string transactionId, ulong round, CancellationToken ct)
    {
        var indexer = IndexerHttpClient;
        if (indexer is null) return false;
        using var targetResponse = await indexer.GetAsync($"/v2/transactions/{Uri.EscapeDataString(transactionId)}", ct);
        if (targetResponse.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        targetResponse.EnsureSuccessStatusCode();
        using var targetDocument = await JsonDocument.ParseAsync(await targetResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!targetDocument.RootElement.TryGetProperty("transaction", out var target)
            || !target.TryGetProperty("id", out var targetId) || targetId.GetString() != transactionId
            || !target.TryGetProperty("application-transaction", out var targetApplication)
            || !targetApplication.TryGetProperty("application-id", out var targetApplicationId)
            || targetApplicationId.GetUInt64().ToString(CultureInfo.InvariantCulture) != input.ApplicationId
            || !target.TryGetProperty("confirmed-round", out var targetRound) || targetRound.GetUInt64() != round
            || !target.TryGetProperty("intra-round-offset", out var targetOffset) || !targetOffset.TryGetUInt64(out var position)) return false;
        var applicationId = ulong.Parse(input.ApplicationId!, CultureInfo.InvariantCulture);
        var transactions = new List<JsonElement>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        for (var page = 0; page < 128; page++)
        {
            var uri = $"/v2/transactions?application-id={applicationId}&max-round={round}&limit=1000";
            if (next is not null) uri += "&next=" + Uri.EscapeDataString(next);
            using var response = await indexer.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            if (!root.TryGetProperty("current-round", out var current) || current.GetUInt64() < round
                || !root.TryGetProperty("transactions", out var items) || items.ValueKind != JsonValueKind.Array) return false;
            transactions.AddRange(items.EnumerateArray().Select(item => item.Clone()));
            if (root.TryGetProperty("next-token", out var token) && token.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) return false;
            next = token.ValueKind == JsonValueKind.String ? token.GetString() : null;
            if (string.IsNullOrEmpty(next)) return ProgramsAtPosition(input, applicationId, transactionId, round, position, transactions);
            if (!seenTokens.Add(next)) return false;
        }
        return false;
    }

    internal static bool ProgramsAtPosition(AvmNativeInput input, ulong applicationId, string transactionId, ulong round, ulong position, IReadOnlyList<JsonElement> transactions)
    {
        var changes = new SortedDictionary<(ulong Round, ulong Offset), (string Approval, string Clear)>();
        var creationSeen = false;
        var targetSeen = false;
        foreach (var transaction in transactions)
        {
            if (!transaction.TryGetProperty("confirmed-round", out var confirmed) || !confirmed.TryGetUInt64(out var atRound)
                || !transaction.TryGetProperty("intra-round-offset", out var intra) || !intra.TryGetUInt64(out var atOffset)) return false;
            if (atRound > round || atRound == round && atOffset > position) continue;
            if (transaction.TryGetProperty("id", out var id) && id.GetString() == transactionId) { targetSeen = true; continue; }
            if (atRound == round && atOffset == position) return false;
            if (HasInnerApplicationMutation(transaction, applicationId)) return false;
            if (!transaction.TryGetProperty("application-transaction", out var application)) continue;
            var create = transaction.TryGetProperty("created-application-index", out var created) && created.TryGetUInt64(out var createdId) && createdId == applicationId;
            var update = application.TryGetProperty("application-id", out var called) && called.TryGetUInt64(out var calledId) && calledId == applicationId
                && application.TryGetProperty("on-completion", out var completion) && completion.GetString() == "update";
            if (!create && !update) continue;
            if (create) creationSeen = true;
            if (!application.TryGetProperty("approval-program", out var approval) || !application.TryGetProperty("clear-state-program", out var clear)) return false;
            var programs = (approval.GetString()!, clear.GetString()!);
            var key = (atRound, atOffset);
            if (changes.TryGetValue(key, out var existing) && existing != programs) return false;
            changes[key] = programs;
        }
        if (!creationSeen || !targetSeen || changes.Count == 0) return false;
        var expected = input.Artifact.Content.Deserialize<AvmApplicationArtifact>(AvmJson)!;
        var active = changes.Last().Value;
        return active.Approval == expected.Approval.Data && active.Clear == expected.ClearState.Data;
    }

    private static bool HasInnerApplicationMutation(JsonElement transaction, ulong applicationId)
    {
        if (!transaction.TryGetProperty("inner-txns", out var inner)) return false;
        foreach (var child in inner.EnumerateArray())
        {
            if (child.TryGetProperty("application-transaction", out var application)
                && application.TryGetProperty("application-id", out var called) && called.TryGetUInt64(out var id) && id == applicationId
                && application.TryGetProperty("on-completion", out var completion) && completion.GetString() is "update" or "delete") return true;
            if (HasInnerApplicationMutation(child, applicationId)) return true;
        }
        return false;
    }
}
