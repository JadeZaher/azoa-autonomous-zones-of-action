using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    private const string DexNonceSearchFormat = "ethereum-dex-nonce-search/1";
    private const string DexNonceWinnerFormat = "ethereum-finalized-nonce-winner/1";
    private const string DexSameCallWinnerFormat = "ethereum-nonce-winner-same-call/1";
    private const int DexHistoricalStateMissLimit = 10;
    private sealed record DexEvmNonceSearch(string Format, string Binding, string Low, string High, int Misses = 0);
    private sealed record DexEvmWinner(string TransactionId, BigInteger Block, string BlockHash, JsonElement Transaction);
    private sealed record DexEvmSearch(DexEvmWinner? Winner, string? Checkpoint, string Detail, bool NeedsOperator = false);

    /// <inheritdoc cref="IDexTransactionModule.ObserveSwapStageAsync"/>
    public async Task<AZOAResult<SwapStageObservation>> ObserveSwapStageAsync(SwapIntent intent, PreparedSwapStage prepared,
        CancellationToken ct = default, SwapStageObservation? previous = null)
    {
        var exact = ValidateSignedSwapStage(intent, prepared.Plan, prepared.SignedPayloadJson);
        if (exact.IsError || exact.Result is not { } candidate || !candidate.TransactionIds.SequenceEqual(prepared.TransactionIds) ||
            candidate.SignedPayloadJson != prepared.SignedPayloadJson)
            return Error<SwapStageObservation>("Saved Ethereum observation identity changed.");
        try { return Ok((await ObserveDexPlanAsync(intent, prepared.Plan, prepared, previous, ct)).Observation); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException)
        { return Ok(DexNeedsOperator(intent, prepared.TransactionIds, "Ethereum evidence differs from the reviewed receipt or code grammar.")); }
    }

    /// <inheritdoc cref="IDexTransactionModule.GetExposedSwapTransactionIds"/>
    public AZOAResult<IReadOnlyList<string>> GetExposedSwapTransactionIds(SwapIntent intent, SwapStagePlan plan)
    {
        try
        {
            ReadEthereumDexPlan(intent, plan);
            return Ok<IReadOnlyList<string>>([]);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException or ArgumentException)
        { return Error<IReadOnlyList<string>>("Ethereum plan differs from its original reservation; type-2 identities depend on the actual signature."); }
    }

    /// <inheritdoc cref="IDexTransactionModule.ObserveExposedSwapStageAsync"/>
    public async Task<AZOAResult<SwapExternalStageObservation>> ObserveExposedSwapStageAsync(SwapIntent intent,
        SwapStagePlan plan, CancellationToken ct = default, SwapStageObservation? previous = null)
    {
        try
        {
            var (observation, recovered) = await ObserveDexPlanAsync(intent, plan, null, previous, ct);
            return Ok(new SwapExternalStageObservation(observation, recovered));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException)
        { return Ok(new SwapExternalStageObservation(DexNeedsOperator(intent, [], "Exposed Ethereum evidence differs from the reviewed receipt or code grammar."))); }
    }

    /// <inheritdoc cref="IDexTransactionModule.ValidateExposedSwapNoEffect"/>
    public bool ValidateExposedSwapNoEffect(SwapIntent intent, SwapStagePlan plan, SwapStageObservation observation)
        => observation.TransactionIds is { Count: 0 } && ValidDexNoEffect(intent, plan, observation);

    /// <inheritdoc cref="IDexTransactionModule.ResolveSwapStageAccountingAsync"/>
    public Task<AZOAResult<SwapStageRentAccounting>> ResolveSwapStageAccountingAsync(SwapIntent intent,
        PreparedSwapStage prepared, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<SwapStageRentAccounting>.Failure("Ethereum DEX stages reserve no account rent; there is no unresolved accounting."));

    /// <inheritdoc cref="IDexTransactionModule.ScanSwapRentReleaseAsync"/>
    public Task<AZOAResult<SwapRentReleaseProgress>> ScanSwapRentReleaseAsync(SwapIntent intent, PreparedSwapStage allocation,
        PreparedSwapStage cleanup, string? checkpointJson, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<SwapRentReleaseProgress>.Failure("Ethereum allowance cleanup releases no rent allocation."));

    private async Task<(SwapStageObservation Observation, PreparedSwapStage? Recovered)> ObserveDexPlanAsync(SwapIntent intent,
        SwapStagePlan plan, PreparedSwapStage? prepared, SwapStageObservation? previous, CancellationToken ct)
    {
        var saved = ReadEthereumDexPlan(intent, plan);
        IReadOnlyList<string> ids = prepared?.TransactionIds ?? [];
        if (prepared is not null && await ReadDexFinalizedAsync(intent, plan, prepared.TransactionIds[0], ct) is { } original)
            return (await DexEffectObservationAsync(intent, plan, original, ct), null);
        var search = await SearchDexNonceWinnerAsync(intent, plan, saved, previous, ct);
        if (search.NeedsOperator) return (DexNeedsOperator(intent, ids, search.Detail) with { EvidenceJson = search.Checkpoint }, null);
        if (search.Winner is not { } winner) return (DexPending(intent, ids, search.Checkpoint, search.Detail), null);
        if (prepared is not null && SameDexHash(winner.TransactionId, prepared.TransactionIds[0]))
            return (DexPending(intent, ids, search.Checkpoint, "The original transaction won the nonce; its complete finalized receipt is not yet proven."), null);
        var identical = false;
        try { EthereumRpcEnvelope.Read(winner.Transaction, DexUnsigned(saved), intent.Terms.Owner.Address); identical = true; }
        catch (InvalidDataException) { }
        if (identical)
        {
            var alternate = await ReadDexFinalizedAsync(intent, plan, winner.TransactionId, ct);
            if (alternate is null)
                return (DexPending(intent, ids, search.Checkpoint, "A signature over the exact plan won the nonce; its finalized receipt is not yet proven."), null);
            var effect = await DexEffectObservationAsync(intent, plan, alternate, ct);
            if (prepared is null) return (effect, alternate.Candidate);
            if (effect.Verdict is not ("Confirmed" or "FailedOnChain")) return (DexNeedsOperator(intent, ids, effect.Detail ?? "Alternate winner evidence is inconsistent."), null);
            return (effect with { FinalizedCandidate = new(alternate.Candidate, CanonicalJson.Hash(prepared), CanonicalJson.Hash(prepared.Plan)) }, null);
        }
        var receipt = await ReadFinalizedReceiptAsync(winner.TransactionId, ct);
        if (receipt is null || receipt.BlockNumber != winner.Block || !SameDexHash(receipt.BlockHash, winner.BlockHash))
            return (DexPending(intent, ids, search.Checkpoint, "A different nonce winner exists but its finalized receipt is not yet proven."), null);
        // Same call in a wallet-chosen envelope (fees, gas or type) may have executed the swap: never a no-effect proof.
        if (SameDexCall(winner.Transaction, saved))
            return (DexSameCallWinner(intent, plan, saved, ids, winner, receipt), null);
        var evidence = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            Format = DexNonceWinnerFormat, PlanHash = CanonicalJson.Hash(plan), saved.ProtocolIdentity, Sender = saved.Call.Sender,
            saved.Nonce, WinnerTransactionId = winner.TransactionId.ToLowerInvariant(), WinnerBlockHash = receipt.BlockHash,
            WinnerBlockNumber = Text(receipt.BlockNumber), WinnerSucceeded = receipt.Succeeded,
        }));
        return (new("ExpiredAbsent", ids, intent.Terms.Network.ProtocolIdentity, "0", "0", "0", "0", "0", evidence,
            "A different finalized transaction consumed the reserved nonce; the original plan can never execute."), null);
    }

    private async Task<DexEvmSearch> SearchDexNonceWinnerAsync(SwapIntent intent, SwapStagePlan plan, DexEvmPlan saved,
        SwapStageObservation? previous, CancellationToken ct)
    {
        var quote = ReadDexAcceptedIntent(intent);
        var chain = await GetChainIdAsync(ct);
        var genesis = await RpcReadAsync("eth_getBlockByNumber", ["0x0", false], ct);
        if (chain.IsError || "eip155:" + Text(chain.Result) != saved.ProtocolIdentity || genesis.IsError || genesis.IsNullResult ||
            !TryReadStringProperty(genesis.Value, "hash", out var actualGenesis) || !SameDexHash(actualGenesis, quote.Deployment.GenesisHash))
            return new(null, previous?.EvidenceJson, "The configured node is not the original physical network.");
        var finalized = await RpcReadAsync("eth_getBlockByNumber", ["finalized", false], ct);
        if (finalized.IsError || finalized.IsNullResult || !TryReadQuantityProperty(finalized.Value, "number", out var head) ||
            !TryReadStringProperty(finalized.Value, "hash", out var headHash) || !IsDexHash(headHash))
            return new(null, previous?.EvidenceJson, "Finalized Ethereum head evidence is unavailable.");
        var nonce = EthereumDexExecutionCodec.Amount(saved.Nonce);
        var consumed = await DexNonceCountAsync(saved.Call.Sender, new { blockHash = headHash, requireCanonical = true }, ct);
        if (consumed is null) return new(null, previous?.EvidenceJson, "Finalized sender nonce state is unavailable.");
        if (consumed <= nonce) return new(null, null, "The reserved nonce is not consumed at finality.");
        var binding = CanonicalJson.Hash(new { Domain = DexNonceSearchFormat, saved.ProtocolIdentity, quote.Deployment.GenesisHash,
            saved.Call.Sender, saved.Nonce, PlanHash = CanonicalJson.Hash(plan) });
        var state = ReadDexNonceSearch(previous?.EvidenceJson, binding) ?? new(DexNonceSearchFormat, binding, quote.BlockNumber, Text(head));
        var low = EthereumDexExecutionCodec.Amount(state.Low); var high = EthereumDexExecutionCodec.Amount(state.High);
        if (low >= high || high > head) return new(null, null, "Saved nonce search differs from finalized boundaries.", true);
        if (state.Low == quote.BlockNumber)
        {
            var floor = await DexNonceCountAsync(saved.Call.Sender, EthereumHex.Quantity(low), ct);
            if (floor is null) return Missed(state);
            if (floor > nonce) return new(null, null, "The reserved nonce was consumed before the original quote block.", true);
        }
        for (var probe = 0; probe < 24 && high - low > 1; probe++)
        {
            var middle = (low + high) / 2;
            var count = await DexNonceCountAsync(saved.Call.Sender, EthereumHex.Quantity(middle), ct);
            if (count is null) return Missed(state with { Low = Text(low), High = Text(high) });
            if (count > nonce) high = middle; else low = middle;
        }
        state = state with { Low = Text(low), High = Text(high), Misses = 0 };
        if (high - low > 1) return new(null, Checkpoint(state), "Finalized nonce-winner search continues on the next pass.");
        var block = await RpcReadAsync("eth_getBlockByNumber", [EthereumHex.Quantity(high), true], ct);
        if (block.IsError || block.IsNullResult || !TryReadQuantityProperty(block.Value, "number", out var number) || number != high ||
            !TryReadStringProperty(block.Value, "hash", out var blockHash) || !IsDexHash(blockHash) ||
            !block.Value.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array)
            return new(null, Checkpoint(state), "The finalized nonce-winner block body is unavailable.");
        var winners = transactions.EnumerateArray().Where(tx => tx.ValueKind == JsonValueKind.Object &&
            TryReadStringProperty(tx, "from", out var from) && EthereumAddress.AreSame(from, saved.Call.Sender) &&
            TryReadQuantityProperty(tx, "nonce", out var txNonce) && txNonce == nonce).ToArray();
        if (winners.Length != 1 || !TryReadStringProperty(winners[0], "hash", out var winnerHash) || !IsDexHash(winnerHash))
            return new(null, Checkpoint(state), "The finalized block does not contain exactly one sender transaction at the reserved nonce.", true);
        return new(new(winnerHash, high, blockHash.ToLowerInvariant(), winners[0].Clone()), Checkpoint(state), "Finalized nonce winner located.");

        static string Checkpoint(DexEvmNonceSearch value) => CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(value));

        // Repeated pruned-state misses mean this node can never finish the proof: escalate rather than wait forever.
        static DexEvmSearch Missed(DexEvmNonceSearch value)
        {
            var missed = value with { Misses = value.Misses + 1 };
            return missed.Misses >= DexHistoricalStateMissLimit
                ? new(null, Checkpoint(missed), "Historical nonce state stayed unavailable; reconfigure an archive node before reconciling this stage.", true)
                : new(null, Checkpoint(missed), "Historical nonce state is unavailable; an archive node is required.");
        }
    }

    private static DexEvmNonceSearch? ReadDexNonceSearch(string? json, string binding)
    {
        if (json is null) return null;
        try
        {
            var state = JsonSerializer.Deserialize<DexEvmNonceSearch>(json);
            return state is { Format: DexNonceSearchFormat } && state.Binding == binding &&
                UnsignedIntegerText.TryParse(state.Low, out _) && UnsignedIntegerText.TryParse(state.High, out _) ? state : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<BigInteger?> DexNonceCountAsync(string sender, object block, CancellationToken ct)
    {
        var response = await RpcReadAsync("eth_getTransactionCount", [sender, block], ct);
        return !response.IsError && response.TryGetString(out var text) && EthereumHex.TryParseQuantity(text, out var count) ? count : null;
    }

    /// <summary>True when the winner calls the same target with the same value and calldata, whatever its type, gas or fees.</summary>
    private static bool SameDexCall(JsonElement transaction, DexEvmPlan saved)
        => TryReadStringProperty(transaction, "to", out var to) && saved.Call.Target is { } target && EthereumAddress.AreSame(to, target) &&
            TryReadQuantityProperty(transaction, "value", out var value) && value == EthereumDexExecutionCodec.Amount(saved.Call.Value) &&
            TryReadStringProperty(transaction, "input", out var input) && input.Equals(saved.Call.Data, StringComparison.OrdinalIgnoreCase);

    private SwapStageObservation DexSameCallWinner(SwapIntent intent, SwapStagePlan plan, DexEvmPlan saved, IReadOnlyList<string> ids,
        DexEvmWinner winner, EthereumFinalizedReceipt receipt)
    {
        var quote = ReadDexAcceptedIntent(intent);
        var logs = receipt.Succeeded ? EthereumDexReceipt.Logs(receipt.Receipt, winner.TransactionId, receipt.BlockHash) : null;
        BigInteger? output = null;
        var proven = false;
        if (logs is not null && plan.Stage == "Swap")
        {
            output = EthereumDexReceipt.SwapOutput(logs, DexSwapExpectation(intent, quote));
            proven = output is not null;
        }
        else if (logs is not null)
            proven = EthereumDexReceipt.ExactApproval(logs, quote.Input.Address, intent.Terms.Owner.Address, quote.Deployment.Executor,
                plan.Stage == "ApproveExact" ? EthereumDexExecutionCodec.Amount(intent.Terms.GrossInput) : BigInteger.Zero);
        var evidence = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            Format = DexSameCallWinnerFormat, PlanHash = CanonicalJson.Hash(plan), saved.ProtocolIdentity, Sender = saved.Call.Sender,
            saved.Nonce, WinnerTransactionId = winner.TransactionId.ToLowerInvariant(), WinnerBlockHash = receipt.BlockHash,
            WinnerBlockNumber = Text(receipt.BlockNumber), WinnerSucceeded = receipt.Succeeded, WinnerNetworkFee = receipt.NetworkFee,
            EffectProven = proven, ActualOutput = output is { } value ? Text(value) : null,
        }));
        return new("NeedsOperator", ids, intent.Terms.Network.ProtocolIdentity, EvidenceJson: evidence,
            Detail: "The reserved nonce executed the exact planned call in a wallet-chosen envelope; an operator must reconcile its finalized receipt and no replacement generation is allowed.");
    }

    private bool ValidDexNoEffect(SwapIntent intent, SwapStagePlan plan, SwapStageObservation observation)
    {
        if (observation.Verdict != "ExpiredAbsent" || observation.ProtocolIdentity != intent.Terms.Network.ProtocolIdentity ||
            observation.ActualInput != "0" || observation.ActualOutput != "0" || observation.PlatformFeePaid != "0" ||
            observation.NetworkFeePaid != "0" || observation.LockedBalance != "0" || observation.EvidenceJson is null ||
            observation.FinalizedCandidate is not null) return false;
        try
        {
            var saved = ReadEthereumDexPlan(intent, plan);
            using var document = JsonDocument.Parse(observation.EvidenceJson);
            var proof = document.RootElement;
            return proof.ValueKind == JsonValueKind.Object && proof.EnumerateObject().Count() == 9 &&
                proof.GetProperty("Format").GetString() == DexNonceWinnerFormat &&
                proof.GetProperty("PlanHash").GetString() == CanonicalJson.Hash(plan) &&
                proof.GetProperty("ProtocolIdentity").GetString() == saved.ProtocolIdentity &&
                proof.GetProperty("Sender").GetString() == saved.Call.Sender && proof.GetProperty("Nonce").GetString() == saved.Nonce &&
                IsDexHash(proof.GetProperty("WinnerTransactionId").GetString() ?? "") &&
                IsDexHash(proof.GetProperty("WinnerBlockHash").GetString() ?? "") &&
                UnsignedIntegerText.TryParse(proof.GetProperty("WinnerBlockNumber").GetString(), out _) &&
                proof.GetProperty("WinnerSucceeded").ValueKind is JsonValueKind.True or JsonValueKind.False;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { return false; }
    }

    private static SwapStageObservation DexPending(SwapIntent intent, IReadOnlyList<string> ids, string? checkpoint, string detail)
        => new("Pending", ids, intent.Terms.Network.ProtocolIdentity, EvidenceJson: checkpoint, Detail: detail);

    private static SwapStageObservation DexNeedsOperator(SwapIntent intent, IReadOnlyList<string> ids, string detail)
        => new("NeedsOperator", ids, intent.Terms.Network.ProtocolIdentity, Detail: detail);
}
