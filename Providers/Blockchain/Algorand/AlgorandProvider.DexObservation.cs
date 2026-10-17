using System.Globalization;
using System.Net;
using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="IDexTransactionModule.ObserveSwapStageAsync"/>
    public async Task<AZOAResult<SwapStageObservation>> ObserveSwapStageAsync(SwapIntent intent,
        PreparedSwapStage prepared, CancellationToken ct = default, SwapStageObservation? previous = null)
    {
        var validated = ValidateSignedSwapStage(intent, prepared.Plan, prepared.SignedPayloadJson);
        if (validated.IsError || validated.Result is not { } exact || !exact.TransactionIds.SequenceEqual(prepared.TransactionIds) ||
            exact.SignedPayloadJson != prepared.SignedPayloadJson)
            return AZOAResult<SwapStageObservation>.Failure("Saved Tinyman observation identity changed.");
        return await ObserveTinymanPlanAsync(intent, prepared.Plan, prepared.TransactionIds, ct);
    }

    private async Task<AZOAResult<SwapStageObservation>> ObserveTinymanPlanAsync(SwapIntent intent, SwapStagePlan plan,
        IReadOnlyList<string> transactionIds, CancellationToken ct)
    {
        var wire = ReadTinymanPlan(intent, plan);
        var parameters = await AlgodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct)
            ?? throw new JsonException("Algod observation parameters are missing.");
        if (parameters.LastRound < 1 || parameters.GenesisHashB64 != wire.GenesisHash)
            return AZOAResult<SwapStageObservation>.Failure("Tinyman observation belongs to another chain genesis.");
        var pending = new SwapStageObservation("Pending", transactionIds, intent.Terms.Network.ProtocolIdentity,
            Detail: "Complete finalized group evidence is not yet available.");
        if (IndexerHttpClient is null) return AZOAResult<SwapStageObservation>.Success(pending);
        var first = await ReadTinymanIndexedTransactionAsync(transactionIds[0], ct);
        if (first is null) return AZOAResult<SwapStageObservation>.Success(pending);
        if (first.Value.Transaction is { } tx)
        {
            if (!TinymanSwapReceipt.Number(tx, "confirmed-round", out var round) || round < wire.FirstValid || round > wire.LastValid ||
                first.Value.IndexedRound < round) return AZOAResult<SwapStageObservation>.Success(pending);
            var block = await ReadTinymanIndexerJsonAsync($"/v2/blocks/{round.ToString(CultureInfo.InvariantCulture)}", ct);
            if (block is null || !TinymanSwapReceipt.Number(block.Value, "round", out var blockRound) || blockRound != round ||
                !TinymanSwapReceipt.TryRead(wire, transactionIds, block.Value, out var observed))
                return AZOAResult<SwapStageObservation>.Success(pending with { Detail = "Indexed group evidence differs from its saved wire or settlement grammar." });
            if (wire.Stage == "Swap" && !await TinymanSavedProgramsMatchAsync(intent, wire, ct))
                return AZOAResult<SwapStageObservation>.Success(pending with { Detail = "The pinned immutable Tinyman validator programs are not proven." });
            return AZOAResult<SwapStageObservation>.Success(observed!);
        }
        if ((ulong)parameters.LastRound <= wire.LastValid || first.Value.IndexedRound < wire.LastValid)
            return AZOAResult<SwapStageObservation>.Success(pending);
        foreach (var id in transactionIds.Skip(1))
        {
            var evidence = await ReadTinymanIndexedTransactionAsync(id, ct);
            if (evidence is null || evidence.Value.Transaction is not null || evidence.Value.IndexedRound < wire.LastValid)
                return AZOAResult<SwapStageObservation>.Success(pending);
        }
        var end = await ReadTinymanIndexerJsonAsync($"/v2/blocks/{wire.LastValid.ToString(CultureInfo.InvariantCulture)}", ct);
        if (end is null || !TinymanSwapReceipt.Number(end.Value, "round", out var endRound) || endRound != wire.LastValid ||
            !TinymanSwapReceipt.Text(end.Value, "genesis-hash", wire.GenesisHash)) return AZOAResult<SwapStageObservation>.Success(pending);
        var absence = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            format = "algorand-complete-group-absence/1", transactionIds, genesisHash = wire.GenesisHash,
            planHash = CanonicalJson.Hash(plan),
            lastValid = wire.LastValid.ToString(CultureInfo.InvariantCulture), expiryBlockDigest = CanonicalJson.Hash(end.Value),
        }));
        return AZOAResult<SwapStageObservation>.Success(new("ExpiredAbsent", transactionIds, intent.Terms.Network.ProtocolIdentity,
            "0", "0", "0", "0", "0", absence));
    }

    /// <inheritdoc cref="IDexTransactionModule.ResolveSwapStageAccountingAsync"/>
    public async Task<AZOAResult<SwapStageRentAccounting>> ResolveSwapStageAccountingAsync(SwapIntent intent,
        PreparedSwapStage prepared, CancellationToken ct = default)
    {
        var validated = ValidateSignedSwapStage(intent, prepared.Plan, prepared.SignedPayloadJson);
        if (validated.IsError || validated.Result is not { } exact || !exact.TransactionIds.SequenceEqual(prepared.TransactionIds) ||
            exact.SignedPayloadJson != prepared.SignedPayloadJson)
            return AZOAResult<SwapStageRentAccounting>.Failure("Saved Tinyman accounting identity changed.");
        var wire = ReadTinymanPlan(intent, prepared.Plan);
        if (wire.Stage != "OptIn" || IndexerHttpClient is null)
            return new() { Message = "This stage has no unresolved opt-in accounting evidence." };
        var indexed = await ReadTinymanIndexedTransactionAsync(prepared.TransactionIds[0], ct);
        if (indexed?.Transaction is not { } transaction || !TinymanSwapReceipt.Number(transaction, "confirmed-round", out var round) ||
            round < 2 || indexed.Value.IndexedRound < round) return new() { Message = "Confirmed opt-in evidence is not available." };
        var block = await ReadTinymanIndexerJsonAsync($"/v2/blocks/{round.ToString(CultureInfo.InvariantCulture)}", ct);
        var account = await ReadTinymanIndexerJsonAsync($"/v2/accounts/{Uri.EscapeDataString(wire.Owner)}?round={(round - 1).ToString(CultureInfo.InvariantCulture)}&include-all=true", ct,
            historicalAccount: true);
        if (block is null || !TinymanSwapReceipt.Number(block.Value, "round", out var blockRound) || blockRound != round ||
            account is null || !TinymanOptInAccounting.TryResolve(wire, prepared.TransactionIds[0], block.Value,
            account.Value, out var actual, out var evidence)) return new() { Message = "Historical account and ordered block evidence are incomplete; retain the full rent reservation." };
        return AZOAResult<SwapStageRentAccounting>.Success(new(actual!, evidence!));
    }

    private async Task<(ulong IndexedRound, JsonElement? Transaction)?> ReadTinymanIndexedTransactionAsync(string id, CancellationToken ct)
    {
        var root = await ReadTinymanIndexerJsonAsync("/v2/transactions?txid=" + Uri.EscapeDataString(id), ct);
        if (root is null || !TinymanSwapReceipt.Number(root.Value, "current-round", out var round) ||
            !root.Value.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array ||
            transactions.GetArrayLength() > 1 ||
            root.Value.TryGetProperty("next-token", out var next) && next.ValueKind != JsonValueKind.Null &&
                (next.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(next.GetString()))) return null;
        if (transactions.GetArrayLength() == 0) return (round, null);
        return TinymanSwapReceipt.Text(transactions[0], "id", id) ? (round, transactions[0].Clone()) : null;
    }

    private async Task<JsonElement?> ReadTinymanIndexerJsonAsync(string path, CancellationToken ct, bool historicalAccount = false)
    {
        if (IndexerHttpClient is not { } indexer) return null;
        using var response = await indexer.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound || historicalAccount && response.StatusCode == HttpStatusCode.BadRequest) return null;
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
    }

    private async Task<bool> TinymanSavedProgramsMatchAsync(SwapIntent intent, TinymanWireParameters wire, CancellationToken ct)
    {
        var application = await ReadTinymanJsonAsync($"/v2/applications/{wire.Validator.ToString(CultureInfo.InvariantCulture)}", ct);
        return application is { } value && TinymanSwapReceipt.Number(value, "id", out var id) && id == wire.Validator &&
            value.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
            ProgramMatches(parameters, "approval-program", intent.Terms.ProtocolEvidence.GetProperty("approvalSha256").GetString()!) &&
            ProgramMatches(parameters, "clear-state-program", intent.Terms.ProtocolEvidence.GetProperty("clearStateSha256").GetString()!);
    }
}
