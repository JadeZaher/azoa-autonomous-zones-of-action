using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Dex;

public sealed partial class SwapOperationService
{
    /// <summary>Operator-only: settles a parked same-call review from re-proven chain evidence; see Services/Dex/AGENTS.md §Operator settlement.</summary>
    public async Task<AZOAResult<SwapOperationView>> SettleOperatorReviewAsync(string operationId, long expectedVersion, Guid operatorId,
        string reason, CancellationToken ct = default)
    {
        if (operationId is not { Length: 64 } || operationId.Any(character => !char.IsAsciiHexDigitLower(character)) || operatorId == Guid.Empty ||
            reason is not { Length: > 0 and <= 500 } || reason != reason.Trim() || reason.Any(char.IsControl))
            return Fail("An exact swap operation, operator identity and a recorded reason are required.");
        var operation = await operations.GetAsync(operationId, ct);
        if (operation?.CurrentStageId is not { } stageId || await operations.GetStageAsync(stageId, ct) is not { ObservationJson: { } recorded } stage)
            return Fail("The swap has no reviewable current stage.");
        if (SwapOperatorSettlementContract.IsOperatorSettlement(JsonSerializer.Deserialize<SwapStageObservation>(recorded)!))
            return AZOAResult<SwapOperationView>.Success(await ProjectAsync(operation, ct));
        if (operation.State != "NeedsOperator" || stage.State != "NeedsOperator" || operation.Version != expectedVersion) return Changed();
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var plan = JsonSerializer.Deserialize<SwapStagePlan>(stage.PlanJson)!;
        var module = modules.Resolve(intent.Terms.Network);
        if (module is not IDexOperatorSettlementModule settlement) return Fail("The original DEX module offers no operator settlement.");
        var audit = new SwapOperatorSettlement(1, operatorId, reason, DateTimeOffset.UtcNow, CanonicalJson.HashCanonical(recorded));
        var proof = await settlement.ProveOperatorSettlementAsync(intent, plan, JsonSerializer.Deserialize<SwapStageObservation>(recorded)!, audit, ct);
        if (proof.IsError || proof.Result is null)
            return AZOAResult<SwapOperationView>.FailureWithCode(proof.Message, proof.Code is { Length: > 0 } code ? code : "Conflict");
        if (await operations.SettleOperatorReviewAsync(operationId, expectedVersion, stageId, stage.Version, proof.Result, ct) is null) return Changed();
        await stages.ReleaseRetainedResourcesAsync(operationId, module, ct);
        return AZOAResult<SwapOperationView>.Success(await ProjectAsync((await operations.GetAsync(operationId, ct))!, ct));
    }
}
