using System.Text.Json;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers.Deployments;

/// <summary>Classifies retained recovery journals before authoritative reobservation and atomic retirement.</summary>
public static class NativeRecoveryReplacement
{
    public static string? ProofKind(DeploymentOperation row, bool inspectSealed = false)
    {
        if (row.ParentOperationId is null || row.SubmissionAuthorized || row.RecoveryProjected
            || (!inspectSealed && row.ReplacedByOperationId is not null) || row.RecoveryChildId is not null) return null;
        List<NativeStageHistory>? history;
        try { history = JsonSerializer.Deserialize<List<NativeStageHistory>>(row.HistoryJson); }
        catch (JsonException) { return null; }
        if (history is null) return null;
        if (row.Generation == 0 && history.Count == 0
            && row.State is "Admitted" or "AwaitingAuthorization" or "Preparing" or "Prepared" or "RecoveryQuoteRequired")
            return "NeverAuthorizedRetirement";
        return row.State == "Failed" && row.PreparedJson is null && history.Count > 0
            && history.All(item => item?.Prepared is not null && item.Observation is not null
                && item.Observation.Verdict is "FailedOnChain" or "ExpiredNoEffect") ? "ResolvedFailure" : null;
    }
}
