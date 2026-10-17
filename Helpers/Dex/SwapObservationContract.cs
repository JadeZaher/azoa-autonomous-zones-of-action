using System.Text.Json;
using AZOA.WebAPI.Models.Dex;

namespace AZOA.WebAPI.Helpers.Dex;

/// <summary>Validates provider verdict shape and economic bounds before immutable journal admission.</summary>
public static class SwapObservationContract
{
    public static bool IsTerminal(string verdict)
        => verdict is "Confirmed" or "FailedOnChain" or "ExpiredAbsent";

    public static bool IsValid(SwapIntent intent, PreparedSwapStage prepared, SwapStageObservation observation)
    {
        if (observation.FinalizedCandidate is not { } winner)
            return IsValid(intent, prepared.Plan, prepared.TransactionIds, observation);
        return winner.Candidate is { Plan: not null, TransactionIds.Count: > 0, SignedPayloadJson.Length: > 0 } &&
            observation.Verdict is ("Confirmed" or "FailedOnChain") &&
            winner.OriginalPreparedHash == CanonicalJson.Hash(prepared) && winner.PlanHash == CanonicalJson.Hash(prepared.Plan) &&
            CanonicalJson.Hash(winner.Candidate.Plan) == winner.PlanHash &&
            !winner.Candidate.TransactionIds.SequenceEqual(prepared.TransactionIds) &&
            IsValid(intent, prepared.Plan, winner.Candidate.TransactionIds, observation);
    }

    public static bool IsValid(SwapIntent intent, SwapStagePlan plan, IReadOnlyList<string> transactionIds,
        SwapStageObservation observation)
    {
        if (observation.ProtocolIdentity != intent.Terms.Network.ProtocolIdentity ||
            observation.TransactionIds is null || !observation.TransactionIds.SequenceEqual(transactionIds)) return false;
        if (observation.FinalizedCandidate is not null && observation.Verdict is not ("Confirmed" or "FailedOnChain")) return false;
        if (observation.Verdict is "Pending" or "NeedsOperator")
            return observation.ActualInput is null && observation.ActualOutput is null && observation.PlatformFeePaid is null &&
                observation.NetworkFeePaid is null && observation.LockedBalance is null;
        if (!IsTerminal(observation.Verdict) ||
            !UnsignedIntegerText.TryParse(observation.NetworkFeePaid, out var networkFee) ||
            !UnsignedIntegerText.TryParse(plan.ReservedNetworkFee, out var reservedFee) || networkFee > reservedFee ||
            !UnsignedIntegerText.TryParse(plan.ReservedLockedBalance, out var reservedLock) ||
            !UnsignedIntegerText.TryParse(observation.PlatformFeePaid, out var platformFee) ||
            !UnsignedIntegerText.TryParse(intent.Terms.PlatformFee.Amount, out var quotedFee) || platformFee > quotedFee ||
            !UnsignedIntegerText.TryParse(observation.ActualInput, out var input) ||
            !UnsignedIntegerText.TryParse(intent.Terms.GrossInput, out var gross) || input > gross ||
            !UnsignedIntegerText.TryParse(observation.ActualOutput, out _) || string.IsNullOrEmpty(observation.EvidenceJson)) return false;
        var knownRent = observation.LockedBalance is not null;
        if (knownRent && (!UnsignedIntegerText.TryParse(observation.LockedBalance, out var locked) || locked > reservedLock)) return false;
        if (!knownRent && (observation.Verdict != "Confirmed" || reservedLock == 0)) return false;
        if (observation.Verdict != "Confirmed" &&
            (input != 0 || observation.ActualOutput != "0" || platformFee != 0 || observation.LockedBalance != "0")) return false;
        if (observation.Verdict == "ExpiredAbsent" && networkFee != 0) return false;
        return HasEvidence(observation.EvidenceJson);
    }

    public static bool HasEvidence(string? json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var evidence = JsonDocument.Parse(json);
            if (evidence.RootElement.ValueKind != JsonValueKind.Object || !evidence.RootElement.EnumerateObject().Any()) return false;
            _ = CanonicalJson.Canonicalize(evidence.RootElement);
            return true;
        }
        catch (JsonException) { return false; }
    }
}
