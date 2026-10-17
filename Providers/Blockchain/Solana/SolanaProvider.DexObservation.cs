using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    /// <inheritdoc cref="IDexTransactionModule.ResolveSwapStageAccountingAsync"/>
    public async Task<AZOAResult<SwapStageRentAccounting>> ResolveSwapStageAccountingAsync(SwapIntent intent,
        PreparedSwapStage prepared, CancellationToken ct = default)
    {
        var observed = await ObserveSwapStageAsync(intent, prepared, ct);
        return !observed.IsError && observed.Result is { Verdict: "Confirmed", LockedBalance: not null, EvidenceJson: not null } receipt
            ? AZOAResult<SwapStageRentAccounting>.Success(new(receipt.LockedBalance, receipt.EvidenceJson))
            : AZOAResult<SwapStageRentAccounting>.Failure("Exact finalized Jupiter rent accounting is not available.");
    }

    /// <inheritdoc cref="IDexTransactionModule.GetExposedSwapTransactionIds"/>
    public AZOAResult<IReadOnlyList<string>> GetExposedSwapTransactionIds(SwapIntent intent, SwapStagePlan plan)
    {
        _ = ReadJupiterPlan(intent, plan);
        return AZOAResult<IReadOnlyList<string>>.Success([]);
    }

    /// <inheritdoc cref="IDexTransactionModule.ObserveSwapStageAsync"/>
    public async Task<AZOAResult<SwapStageObservation>> ObserveSwapStageAsync(SwapIntent intent,
        PreparedSwapStage prepared, CancellationToken ct = default, SwapStageObservation? previous = null)
    {
        var valid = ValidateSignedSwapStage(intent, prepared.Plan, prepared.SignedPayloadJson);
        if (valid.IsError || valid.Result is not { } exact || exact.SignedPayloadJson != prepared.SignedPayloadJson ||
            !exact.TransactionIds.SequenceEqual(prepared.TransactionIds))
            return AZOAResult<SwapStageObservation>.Failure("Saved Jupiter observation wire or identity changed.");
        var observed = await ObserveJupiterAsync(intent, prepared.Plan, exact, previous, ct);
        return AZOAResult<SwapStageObservation>.Success(observed.Observation);
    }

    /// <inheritdoc cref="IDexTransactionModule.ObserveExposedSwapStageAsync"/>
    public async Task<AZOAResult<SwapExternalStageObservation>> ObserveExposedSwapStageAsync(SwapIntent intent,
        SwapStagePlan plan, CancellationToken ct = default, SwapStageObservation? previous = null)
        => AZOAResult<SwapExternalStageObservation>.Success(await ObserveJupiterAsync(intent, plan, null, previous, ct));

    private async Task<SwapExternalStageObservation> ObserveJupiterAsync(SwapIntent intent, SwapStagePlan plan,
        PreparedSwapStage? prepared, SwapStageObservation? previous, CancellationToken ct)
    {
        var ids = prepared?.TransactionIds ?? [];
        try
        {
            var (saved, compiled) = ReadJupiterPlan(intent, plan);
            await VerifyGenesisAsync(ct);
            var candidate = prepared ?? await DiscoverJupiterSignatureAsync(intent, plan, compiled, ct);
            if (candidate is not null)
            {
                var transaction = await ReadSolanaFinalizedTransactionAsync(candidate.TransactionIds.Single(), ct);
                if (transaction.ValueKind != JsonValueKind.Null)
                    return new(await ReadJupiterSettlementAsync(intent, saved, compiled, candidate, transaction, ct),
                        prepared is null ? candidate : null);
            }
            var scan = await ScanJupiterHistoryAsync(intent, plan, saved, compiled, previous, ct);
            if (scan.Candidate is { } recovered)
            {
                var transaction = await ReadSolanaFinalizedTransactionAsync(recovered.TransactionIds.Single(), ct);
                if (transaction.ValueKind == JsonValueKind.Null)
                    return new(new("Pending", recovered.TransactionIds, intent.Terms.Network.ProtocolIdentity,
                        Detail: "The original signed message was found; finalized transaction metadata is not yet available."),
                        prepared is null ? recovered : null);
                return new(await ReadJupiterSettlementAsync(intent, saved, compiled, recovered, transaction, ct),
                    prepared is null ? recovered : null);
            }
            var evidence = scan.Cursor is null ? null : CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
            {
                format = "jupiter-finalized-ancestry/1", history = scan.Cursor
            }));
            var complete = scan.Cursor?.Complete == true;
            return new(new(complete ? "ExpiredAbsent" : "Pending", ids, intent.Terms.Network.ProtocolIdentity,
                complete ? "0" : null, complete ? "0" : null, complete ? "0" : null,
                complete ? "0" : null, complete ? "0" : null, evidence,
                complete ? "Complete finalized block ancestry proves the original message absent after expiry."
                    : "The original message remains under finalized observation; reservations are retained."));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or
            OverflowException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return new(new("NeedsOperator", ids, intent.Terms.Network.ProtocolIdentity,
                Detail: "Finalized Jupiter evidence is missing, inconsistent or outside the reviewed grammar."));
        }
    }

    private async Task<SwapStageObservation> ReadJupiterSettlementAsync(SwapIntent intent, JupiterSavedPlan saved,
        JupiterCompilation compiled, PreparedSwapStage prepared, JsonElement transaction, CancellationToken ct)
    {
        var signed = JsonSerializer.Deserialize<string>(prepared.SignedPayloadJson)!;
        var finalized = await RequireSolanaFinalityAsync(transaction, prepared.TransactionIds.Single(), signed,
            Convert.ToBase64String(compiled.Message), compiled.LookupTables, UInt64Text.Parse(saved.NetworkFee), ct);
        var fee = finalized.Fee.ToString(CultureInfo.InvariantCulture);
        if (finalized.Failed)
            return new("FailedOnChain", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity, "0", "0", "0", fee, "0",
                CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new { format = "jupiter-revert/1", finalized.BlockIdentity,
                    PlanHash = CanonicalJson.Hash(prepared.Plan), Error = finalized.Meta.GetProperty("err") })));
        if (prepared.Plan.Purpose == "Cleanup")
        {
            var returned = JupiterCleanupEvidence.Read(intent.Terms.Owner.Address, prepared.Plan.CleanupConsent!.AssetId,
                checked((byte)intent.Terms.OutputAsset.Decimals), finalized.Accounts, finalized.Meta, finalized.Fee);
            return new("Confirmed", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity, "0", "0", "0", fee, "0",
                CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new { format = "jupiter-cleanup/1", finalized.BlockIdentity,
                    finalized.Slot, PlanHash = CanonicalJson.Hash(prepared.Plan), ReturnedLamports = returned.ToString(CultureInfo.InvariantCulture) })));
        }
        var pins = JupiterAcceptedQuote.Read(intent.Terms);
        var settlement = JupiterSettlementEvidence.Read(intent.Terms, saved.Build, compiled.Instructions,
            compiled.NativeAccount, pins.TokenRent, finalized.Accounts, finalized.Meta, finalized.Fee);
        if (settlement.RetainedRent > UInt64Text.Parse(saved.ReservedRent))
            throw new InvalidDataException("Actual Jupiter rent exceeds its immutable reservation.");
        return new("Confirmed", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity,
            settlement.Input.ToString(CultureInfo.InvariantCulture), settlement.Output.ToString(CultureInfo.InvariantCulture),
            settlement.PlatformFee.ToString(CultureInfo.InvariantCulture), fee, settlement.RetainedRent.ToString(CultureInfo.InvariantCulture),
            CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new { format = "jupiter-settlement/1", finalized.BlockIdentity,
                PlanHash = CanonicalJson.Hash(prepared.Plan), Settlement = settlement })));
    }

    private async Task<PreparedSwapStage?> DiscoverJupiterSignatureAsync(SwapIntent intent, SwapStagePlan plan,
        JupiterCompilation compiled, CancellationToken ct)
    {
        var signatures = await DurableRpcAsync("getSignaturesForAddress", [intent.Terms.Owner.Address,
            new { commitment = "finalized", limit = 25 }], ct);
        if (signatures.ValueKind != JsonValueKind.Array || signatures.GetArrayLength() > 25)
            throw new InvalidDataException("Finalized owner signature history is missing or exceeds the requested page.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in signatures.EnumerateArray())
        {
            var id = entry.GetProperty("signature").GetString()!;
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) throw new InvalidDataException("Signature history repeats an invalid cursor identity.");
            var transaction = await ReadSolanaFinalizedTransactionAsync(id, ct);
            if (transaction.ValueKind == JsonValueKind.Null) continue;
            var encoded = transaction.GetProperty("transaction");
            if (encoded.ValueKind != JsonValueKind.Array || encoded.GetArrayLength() != 2 || encoded[1].GetString() != "base64")
                throw new InvalidDataException("Exact signature history transaction bytes are missing.");
            var bytes = Convert.FromBase64String(encoded[0].GetString()!);
            if (bytes.Length == 0 || bytes[0] is 0 or > 127 || bytes.Length <= 1 + bytes[0] * 64)
                throw new InvalidDataException("Signature history transaction framing is invalid.");
            if (!bytes.AsSpan(1 + bytes[0] * 64).SequenceEqual(compiled.Message)) continue;
            var candidate = ValidateSignedSwapStage(intent, plan, JsonSerializer.Serialize(Convert.ToBase64String(bytes)));
            if (candidate.IsError || candidate.Result is null || candidate.Result.TransactionIds.Single() != id)
                throw new InvalidDataException("Signature history changed the exact owner signature.");
            return candidate.Result;
        }
        return null;
    }

    /// <inheritdoc cref="IDexTransactionModule.ValidateExposedSwapNoEffect"/>
    public bool ValidateExposedSwapNoEffect(SwapIntent intent, SwapStagePlan plan, SwapStageObservation observation)
    {
        try
        {
            var (saved, _) = ReadJupiterPlan(intent, plan);
            if (observation.Verdict != "ExpiredAbsent" || observation.TransactionIds.Count != 0 ||
                observation.ProtocolIdentity != intent.Terms.Network.ProtocolIdentity || observation.ActualInput != "0" ||
                observation.ActualOutput != "0" || observation.PlatformFeePaid != "0" || observation.NetworkFeePaid != "0" || observation.LockedBalance != "0") return false;
            using var evidence = JsonDocument.Parse(observation.EvidenceJson ?? "null");
            if (evidence.RootElement.GetProperty("format").GetString() != "jupiter-finalized-ancestry/1") return false;
            var cursor = evidence.RootElement.GetProperty("history").Deserialize<JupiterHistoryCursor>();
            if (cursor is null || !cursor.Complete || cursor.ScannedBlocks == 0) return false;
            RequireJupiterCursor(cursor, CanonicalJson.Hash(plan), saved.Recovery);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or
            OverflowException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return false; }
    }
}
