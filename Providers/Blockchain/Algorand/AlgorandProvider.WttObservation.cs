using System.Globalization;
using System.Net;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="IWttSourceModule.ObserveWttSourceAsync"/>
    public async Task<AZOAResult<WttStageObservation>> ObserveWttSourceAsync(WttOperationIntent intent,
        WttStageQuote quote, WttStagePreparation prepared, CancellationToken ct = default)
    {
        try
        {
            var (plan, wire) = await ReadWttAlgoSavedSourceAsync(intent, quote, prepared, ct);
            var binding = new WttPreparationBinding(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId,
                intent.OperationId, "Source", prepared.Stage, prepared.Generation, quote.IntentHash, quote.QuoteId);
            var preparationId = WttPreparationIdentity.Id(binding);
            var step = plan.Steps.Single(item => item.Stage == prepared.Stage);
            var expected = BuildWttAlgoSourceStep(intent, plan, step, wire.FirstValid, preparationId);
            var parameters = await ReadWttAlgorandJsonAsync("/v2/transactions/params", ct);
            var head = WttAlgoUnsigned(parameters, "last-round");
            if (head == 0 || WttAlgoText(parameters, "genesis-hash") != plan.GenesisHash
                || WttAlgoText(parameters, "genesis-id") != plan.GenesisId)
                return Error<WttStageObservation>("WTT observation endpoint differs from the saved physical network.");
            var pending = new WttStageObservation("Pending", prepared.TransactionId, prepared.ProtocolIdentity,
                Detail: "Exact finalized group and historical application evidence are still being reconciled.");
            var indexed = await ReadWttAlgoIndexedTransactionAsync(prepared.TransactionId, ct);
            if (indexed is null) return Ok(pending);
            if (indexed.Value.Transaction is not { } first)
                return await ObserveWttAlgoAbsenceAsync(intent, quote, prepared, plan.GenesisHash, wire.FirstValid, expected,
                    head, indexed.Value.Round, pending, ct);
            var round = WttAlgoUnsigned(first, "confirmed-round");
            if (round < wire.FirstValid || round > checked(wire.FirstValid + 1000) || indexed.Value.Round < round || head < round)
                return Ok(pending);
            var block = await ReadWttAlgoIndexerAsync("/v2/blocks/" + round.ToString(CultureInfo.InvariantCulture), ct);
            var receipt = block is null ? null : AlgorandGroupReceipt.Read(expected, block.Value);
            if (receipt is null || receipt.Round != round || receipt.Timestamp > uint.MaxValue) return Ok(pending);
            var identity = await ReadWttAlgoFinalizedBlockIdentityAsync(plan.GenesisHash, receipt.Round, receipt.Timestamp, ct);
            if (identity is null) return Ok(pending);
            var programs = true;
            for (var index = 0; index < expected.Count; index++)
            {
                if (expected[index] is not ApplicationCallTransaction app) continue;
                if (AlgorandApplicationCall.Id(app) is not { } application) return Error<WttStageObservation>("Unknown application in saved WTT group.");
                var core = application == plan.CoreApplication;
                if (!core && application != plan.TokenApplication) return Error<WttStageObservation>("Unknown application in saved WTT group.");
                var transaction = receipt.Transactions[index];
                programs &= await ProveWttAlgoProgramAsync(new(1, preparationId, plan.GenesisHash, application,
                    expected[index].TxID(), "", round, WttAlgoUnsigned(transaction, "intra-round-offset"),
                    core ? plan.CoreApprovalHash : plan.TokenApprovalHash, core ? plan.CoreClearHash : plan.TokenClearHash), ct);
            }
            WttPublication? publication = null;
            if (prepared.Stage is "Attest" or "Transfer")
            {
                var transaction = receipt.Transactions[^1];
                publication = WttAlgorandPublication.Read(transaction, intent, prepared.Stage, expected[^1].TxID(), identity, (uint)receipt.Timestamp);
                if (publication is null) return Ok(pending with { Detail = "The exact core publication is not proven by the finalized group." });
                programs &= await ProveWttAlgoProgramAsync(new(1, preparationId, plan.GenesisHash, plan.CoreApplication,
                    expected[^1].TxID(), publication.EventIndex, round, WttAlgoUnsigned(transaction, "intra-round-offset"),
                    plan.CoreApprovalHash, plan.CoreClearHash), ct);
            }
            if (!programs) return Ok(pending);
            var repeated = await ReadWttAlgoIndexerAsync("/v2/blocks/" + round.ToString(CultureInfo.InvariantCulture), ct);
            if (repeated is null || CanonicalJson.Hash(repeated.Value) != receipt.BlockDigest) return Ok(pending);
            var evidence = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
            {
                format = "wormhole-algorand-source/1", preparationId, quote.IntentHash, quote.QuoteId,
                round = round.ToString(CultureInfo.InvariantCulture), blockIdentity = identity, receipt.BlockDigest,
                transactionIds = expected.Select(transaction => transaction.TxID()).ToArray(),
                coreApproval = plan.CoreApprovalHash, coreClear = plan.CoreClearHash,
                tokenApproval = plan.TokenApprovalHash, tokenClear = plan.TokenClearHash,
                publicationBody = publication?.BodyDigest,
            }));
            return Ok(new WttStageObservation("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity, evidence,
                publication, receipt.NetworkFee.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or ArgumentException or OverflowException)
        { return Error<WttStageObservation>(exception.Message); }
    }

    private async Task<AZOAResult<WttStageObservation>> ObserveWttAlgoAbsenceAsync(WttOperationIntent intent,
        WttStageQuote quote, WttStagePreparation prepared, string genesis, ulong firstValid,
        IReadOnlyList<Transaction> expected, ulong head, ulong indexedRound, WttStageObservation pending, CancellationToken ct)
    {
        var last = checked(firstValid + 1000);
        if (head <= last || indexedRound < last) return Ok(pending);
        foreach (var transaction in expected.Skip(1))
        {
            var indexed = await ReadWttAlgoIndexedTransactionAsync(transaction.TxID(), ct);
            if (indexed is null || indexed.Value.Round < last || indexed.Value.Transaction is not null) return Ok(pending);
        }
        var block = await ReadWttAlgoIndexerAsync("/v2/blocks/" + last.ToString(CultureInfo.InvariantCulture), ct);
        if (block is null || WttAlgoUnsigned(block.Value, "round") != last
            || WttAlgoText(block.Value, "genesis-hash") != genesis) return Ok(pending);
        var identity = await ReadWttAlgoFinalizedBlockIdentityAsync(genesis, last, WttAlgoUnsigned(block.Value, "timestamp"), ct);
        if (identity is null) return Ok(pending);
        var evidence = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            format = "wormhole-algorand-exhausted-absence/1", intent.OperationId, quote.IntentHash, quote.QuoteId,
            prepared.Stage, prepared.Generation, transactionIds = expected.Select(transaction => transaction.TxID()).ToArray(),
            genesis, lastValid = last.ToString(CultureInfo.InvariantCulture), blockIdentity = identity,
            blockDigest = CanonicalJson.Hash(block.Value),
        }));
        return Ok(new WttStageObservation("ExpiredNoEffect", prepared.TransactionId, prepared.ProtocolIdentity, evidence, NetworkFeePaid: "0"));
    }

    private async Task<string?> ReadWttAlgoFinalizedBlockIdentityAsync(string genesis, ulong round, ulong timestamp, CancellationToken ct)
    {
        var root = await ReadWttAlgorandJsonAsync($"/v2/blocks/{round.ToString(CultureInfo.InvariantCulture)}?format=json", ct);
        if (!root.TryGetProperty("block", out var block) || block.ValueKind != JsonValueKind.Object
            || WttAlgoUnsigned(block, "rnd") != round || WttAlgoUnsigned(block, "ts") != timestamp
            || WttAlgoText(block, "gh") != genesis) return null;
        var hash = WttAlgoText(await ReadWttAlgorandJsonAsync($"/v2/blocks/{round.ToString(CultureInfo.InvariantCulture)}/hash", ct), "blockHash");
        return hash.Length == 52 && hash.All(value => value is >= 'A' and <= 'Z' or >= '2' and <= '7') ? hash : null;
    }

    private async Task<(ulong Round, JsonElement? Transaction)?> ReadWttAlgoIndexedTransactionAsync(string id, CancellationToken ct)
    {
        var page = await ReadWttAlgoIndexerAsync("/v2/transactions?txid=" + Uri.EscapeDataString(id), ct);
        if (page is null || !page.Value.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array
            || transactions.GetArrayLength() > 1) return null;
        if (page.Value.TryGetProperty("next-token", out var next) && next.ValueKind != JsonValueKind.Null
            && (next.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(next.GetString()))) return null;
        var round = WttAlgoUnsigned(page.Value, "current-round");
        if (transactions.GetArrayLength() == 0) return (round, null);
        return WttAlgoText(transactions[0], "id") == id ? (round, transactions[0].Clone()) : null;
    }

    private async Task<JsonElement?> ReadWttAlgoIndexerAsync(string path, CancellationToken ct)
    {
        if (IndexerHttpClient is not { } indexer) return null;
        using var response = await indexer.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
        _ = CanonicalJson.Canonicalize(document.RootElement);
        return document.RootElement.Clone();
    }
}
