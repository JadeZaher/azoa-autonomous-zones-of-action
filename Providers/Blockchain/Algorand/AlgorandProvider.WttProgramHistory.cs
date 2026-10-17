using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    private sealed record WttAlgoHistoryBinding(int Version, string PreparationId, string Genesis,
        ulong Application, string RootId, string InnerPath, ulong Round, ulong Offset, string ApprovalHash, string ClearHash);

    private async Task<bool> ProveWttAlgoProgramAsync(WttAlgoHistoryBinding binding, CancellationToken ct)
    {
        if (IndexerHttpClient is not { } indexer) return false;
        using var scope = _custodyScopeFactory?.CreateScope()
            ?? throw new InvalidOperationException("Durable WTT historical evidence storage is required.");
        var store = scope.ServiceProvider.GetRequiredService<IWttEvidenceProgressStore>();
        var initial = new WttAlgorandHistoryPage.State(1, [], 0, 0, null, null);
        var row = await store.ClaimAsync(CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding)),
            CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(initial)), ct);
        var state = JsonSerializer.Deserialize<WttAlgorandHistoryPage.State>(row.StateJson)
            ?? throw new InvalidDataException("WTT application history state is missing.");
        if (state.Version != 1 || state.RelevantRoots is null)
            throw new InvalidDataException("Unknown WTT application history state.");
        if (!row.Complete)
        {
            var uri = "/v2/transactions?application-id=" + binding.Application.ToString(CultureInfo.InvariantCulture)
                + "&max-round=" + binding.Round.ToString(CultureInfo.InvariantCulture) + "&limit=25";
            if (row.NextCursor is not null) uri += "&next=" + Uri.EscapeDataString(row.NextCursor);
            using var response = await indexer.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var page = document.RootElement;
            _ = CanonicalJson.Canonicalize(page);
            if (WttAlgoUnsigned(page, "current-round") < binding.Round || !page.TryGetProperty("transactions", out var transactions)
                || transactions.ValueKind != JsonValueKind.Array || transactions.GetArrayLength() > 25) return false;
            string? next = null;
            if (page.TryGetProperty("next-token", out var token) && token.ValueKind != JsonValueKind.Null)
            {
                if (token.ValueKind != JsonValueKind.String) return false;
                next = string.IsNullOrEmpty(token.GetString()) ? null : token.GetString();
            }
            var following = WttAlgorandHistoryPage.Advance(binding.Application, binding.RootId, binding.Round, binding.Offset, state, transactions);
            var saved = await store.AdvanceAsync(row, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(following)),
                CanonicalJson.Hash(page), next, ct);
            if (saved is null || !saved.Complete) return false;
            state = following;
        }
        return WttAlgorandProgramHistory.Matches(state.RelevantRoots, binding.Application, binding.RootId, binding.InnerPath,
            binding.Round, binding.Offset, binding.ApprovalHash, binding.ClearHash);
    }

}
