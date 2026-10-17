using System.Text.Json;
using AZOA.WebAPI.Models.Dex;

namespace AZOA.WebAPI.Helpers.Dex;

/// <summary>Journal shape of an operator-settled stage: one finalized winner, exact terminal amounts and an audit member.</summary>
/// <remarks>See Services/Dex/AGENTS.md §Operator settlement.</remarks>
public static class SwapOperatorSettlementContract
{
    /// <summary>Evidence member carrying the immutable <see cref="SwapOperatorSettlement"/> audit.</summary>
    public const string AuditMember = "OperatorSettlement";

    /// <summary>True when the observation claims operator settlement (validity is checked separately).</summary>
    public static bool IsOperatorSettlement(SwapStageObservation observation) => ReadAudit(observation) is not null;

    /// <summary>Reads the audit member, or null when absent or malformed.</summary>
    public static SwapOperatorSettlement? ReadAudit(SwapStageObservation observation)
    {
        if (string.IsNullOrEmpty(observation.EvidenceJson)) return null;
        try
        {
            using var evidence = JsonDocument.Parse(observation.EvidenceJson);
            return evidence.RootElement.ValueKind == JsonValueKind.Object && evidence.RootElement.TryGetProperty(AuditMember, out var member)
                ? member.Deserialize<SwapOperatorSettlement>() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Validates the provider-neutral shape; the actual winner fee is deliberately not bounded by the stage reservation.</summary>
    public static bool IsValid(SwapIntent intent, SwapStagePlan plan, SwapStageObservation observation)
    {
        if (ReadAudit(observation) is not { Version: 1 } audit || audit.OperatorId == Guid.Empty ||
            audit.Reason is not { Length: > 0 and <= 500 } || audit.Reason != audit.Reason.Trim() || audit.Reason.Any(char.IsControl) ||
            audit.ReplacedObservationHash is not { Length: 64 } || !audit.ReplacedObservationHash.All(char.IsAsciiHexDigitLower) ||
            observation.Verdict is not ("Confirmed" or "FailedOnChain") || observation.FinalizedCandidate is not null ||
            observation.ProtocolIdentity != intent.Terms.Network.ProtocolIdentity ||
            observation.TransactionIds is not { Count: 1 } || string.IsNullOrWhiteSpace(observation.TransactionIds[0]) ||
            !UnsignedIntegerText.TryParse(observation.NetworkFeePaid, out _) ||
            !UnsignedIntegerText.TryParse(observation.LockedBalance, out var locked) ||
            !UnsignedIntegerText.TryParse(plan.ReservedLockedBalance, out var reservedLock) || locked > reservedLock ||
            !UnsignedIntegerText.TryParse(observation.PlatformFeePaid, out var platformFee) ||
            !UnsignedIntegerText.TryParse(intent.Terms.PlatformFee.Amount, out var quotedFee) || platformFee > quotedFee ||
            !UnsignedIntegerText.TryParse(observation.ActualInput, out var input) ||
            !UnsignedIntegerText.TryParse(intent.Terms.GrossInput, out var gross) || input > gross ||
            !UnsignedIntegerText.TryParse(observation.ActualOutput, out _)) return false;
        if (observation.Verdict != "Confirmed" &&
            (input != 0 || observation.ActualOutput != "0" || platformFee != 0 || locked != 0)) return false;
        return SwapObservationContract.HasEvidence(observation.EvidenceJson);
    }
}
