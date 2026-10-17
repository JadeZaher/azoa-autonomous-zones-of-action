using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="IExchangeTransactionModule.SubmitExchangeAsync"/>
    public async Task SubmitExchangeAsync(PreparedExchangeWire prepared, CancellationToken ct = default)
    {
        var transactions = await SavedExchangeAsync(prepared, ct);
        var parameters = await ValidatedTransactionParametersAsync(ct);
        RequireExchangeNetwork(prepared, transactions, parameters);
        if ((ulong)parameters.LastRound > transactions[0].LastValid) return;
        var minimum = transactions.Aggregate(0UL, (fee, transaction) => checked(fee + AvmFee(transaction, parameters)));
        if (transactions[^1].Fee < minimum) return;
        using var content = new ByteArrayContent(prepared.SignedTransactionsBase64.SelectMany(Convert.FromBase64String).ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-binary");
        using var response = await AlgodHttpClient.PostAsync("/v2/transactions", content, ct);
        response.EnsureSuccessStatusCode();
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!AlgorandReceiptFields.String(result.RootElement, "txId", prepared.TransactionIds[0]))
            throw new HttpRequestException("Algod did not acknowledge the saved exchange transaction identity.");
    }

    /// <inheritdoc cref="IExchangeTransactionModule.ObserveExchangeAsync"/>
    public async Task<AZOAResult<ExchangeWireObservation>> ObserveExchangeAsync(PreparedExchangeWire prepared, CancellationToken ct = default)
    {
        var transactions = await SavedExchangeAsync(prepared, ct);
        var parameters = await ValidatedTransactionParametersAsync(ct);
        RequireExchangeNetwork(prepared, transactions, parameters);
        ExchangeWireObservation Verdict(string verdict, ulong? round = null) => new(verdict, prepared.PreparationId,
            prepared.GroupId, CanonicalJson.Hash(prepared), prepared.Intent.ProtocolIdentity, round?.ToString(CultureInfo.InvariantCulture));
        var unknown = AZOAResult<ExchangeWireObservation>.Success(Verdict("Unknown"));
        if (IndexerHttpClient is not { } indexer) return unknown;
        var results = new List<JsonElement>();
        var minimumIndexedRound = ulong.MaxValue;
        foreach (var id in prepared.TransactionIds)
        {
            using var response = await indexer.GetAsync($"/v2/transactions?txid={Uri.EscapeDataString(id)}", ct);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("current-round", out var indexed)
                || indexed.ValueKind != JsonValueKind.Number || !indexed.TryGetUInt64(out var currentRound)
                || !root.TryGetProperty("transactions", out var found) || found.ValueKind != JsonValueKind.Array
                || found.GetArrayLength() > 1 || HasExchangeContinuation(root)) return unknown;
            minimumIndexedRound = Math.Min(minimumIndexedRound, currentRound);
            if (found.GetArrayLength() == 1)
            {
                var transaction = found[0];
                if (!AlgorandReceiptFields.String(transaction, "id", id)) return unknown;
                results.Add(transaction.Clone());
            }
        }
        var lastValid = transactions[0].LastValid!.Value;
        if (results.Count == 0)
        {
            if (minimumIndexedRound <= lastValid || (ulong)parameters.LastRound <= lastValid) return unknown;
            using var response = await indexer.GetAsync($"/v2/blocks/{lastValid}", ct);
            response.EnsureSuccessStatusCode();
            using var block = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return ExchangeBlockMatches(block.RootElement, prepared, lastValid)
                ? AZOAResult<ExchangeWireObservation>.Success(Verdict("ExpiredAbsent")) : unknown;
        }
        if (results.Count != transactions.Length || !results[0].TryGetProperty("confirmed-round", out var confirmed)
            || confirmed.ValueKind != JsonValueKind.Number || !confirmed.TryGetUInt64(out var round)
            || round == 0 || round < transactions[0].FirstValid || round > lastValid || minimumIndexedRound < round) return unknown;
        using var blockResponse = await indexer.GetAsync($"/v2/blocks/{round}", ct);
        blockResponse.EnsureSuccessStatusCode();
        using var blockDocument = await JsonDocument.ParseAsync(await blockResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var group = AlgorandGroupReceipt.Read(transactions, blockDocument.RootElement);
        if (group is null || group.Round != round) return unknown;
        return AZOAResult<ExchangeWireObservation>.Success(Verdict("Confirmed", round));
    }

    private async Task<Transaction[]> SavedExchangeAsync(PreparedExchangeWire prepared, CancellationToken ct)
    {
        var saved = await NativePreparationJournal.UseAsync(_custodyScopeFactory,
            store => store.GetAsync(prepared.PreparationId, ct));
        if (saved is null || saved.Stage != "Exchange" || saved.Generation != prepared.Intent.Generation
            || saved.IntentHash != CanonicalJson.Hash(prepared.Intent) || saved.PreparedJson != JsonSerializer.Serialize(prepared))
            throw new InvalidOperationException("Exchange wire is not the original durable prepared candidate.");
        var plan = JsonSerializer.Deserialize<ExchangeUnsignedPlan>(saved.PlanJson)
            ?? throw new InvalidOperationException("Exchange unsigned plan is absent.");
        return ValidateExchangePrepared(prepared, plan);
    }

    private static void RequireExchangeNetwork(PreparedExchangeWire prepared, Transaction[] transactions, AlgodSuggestedParams parameters)
    {
        if (prepared.Intent.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64
            || transactions.Any(transaction => transaction.GenesisId != parameters.GenesisId))
            throw new InvalidOperationException("Exchange endpoint is on another physical network.");
    }

    private static bool HasExchangeContinuation(JsonElement root)
        => root.TryGetProperty("next-token", out var token) && token.ValueKind != JsonValueKind.Null
            && (token.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(token.GetString()));

    private static bool ExchangeBlockMatches(JsonElement block, PreparedExchangeWire prepared, ulong round)
        => AlgorandReceiptFields.Number(block, "round", round)
            && AlgorandReceiptFields.String(block, "genesis-hash", prepared.Intent.ProtocolIdentity["algorand:".Length..]);

}
