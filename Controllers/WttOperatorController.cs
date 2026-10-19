using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Services.NodeAdmin;

namespace AZOA.WebAPI.Controllers;

/// <summary>Operator inspection and reconciliation; reconciliation only observes and replays already-authorized wire.</summary>
[ApiController]
[Route("api/operator/wtt")]
[Authorize(Policy = "RecentNodeOperatorSession")]
[EnableRateLimiting("financial")]
public sealed class WttOperatorController(WttLifecycleService lifecycle, WttRecoveryService recovery, WttStageKeyReadiness stageKeys,
    ILogger<WttOperatorController> logger) : ControllerBase
{
    /// <summary>Live keyring readiness; a degraded keyring refuses new stage-key WTT admissions until restored.</summary>
    [HttpGet("stage-keys/readiness")]
    public async Task<IActionResult> StageKeyReadiness(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(AZOAResult<WttStageKeyReadinessReport>.Success(await stageKeys.CheckAsync(ct)));
    }

    /// <summary>One bounded CAS rewrap pass onto the active key; repeat until no rewrap is pending, then retire old keys.</summary>
    [HttpPost("stage-keys/rewrap")]
    public async Task<IActionResult> RewrapStageKeys(CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Unauthorized();
        logger.LogWarning("Operator {OperatorId} started a WTT stage-key rewrap pass", operatorActor.AuditActor);
        return Ok(AZOAResult<WttStageKeyRewrapReport>.Success(await stageKeys.RewrapAsync(ct)));
    }

    [HttpGet("operations/{id}")]
    public async Task<IActionResult> Inspect(string id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var view = await lifecycle.InspectAsync(id, ct);
        return view is null ? NotFound() : Ok(AZOAResult<WttOperationView>.Success(view));
    }

    [HttpPost("operations/{id}/reconcile")]
    public async Task<IActionResult> Reconcile(string id, CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Unauthorized();
        logger.LogWarning("Operator {OperatorId} reconciled WTT operation {OperationId}", operatorActor.AuditActor, id);
        if (await lifecycle.InspectAsync(id, ct) is null) return NotFound();
        await recovery.ReconcileAsync(id, ct);
        return Ok(AZOAResult<WttOperationView>.Success((await lifecycle.InspectAsync(id, ct))!));
    }

    [HttpPost("operations/{id}/refetch-proof")]
    public async Task<IActionResult> RefetchProof(string id, CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Unauthorized();
        logger.LogWarning("Operator {OperatorId} re-fetched the WTT guardian proof for {OperationId}", operatorActor.AuditActor, id);
        if (await lifecycle.InspectAsync(id, ct) is null) return NotFound();
        if (!await recovery.RefetchProofAsync(id, ct))
            return Conflict(AZOAResult<object>.FailureWithCode(
                "Only a target-phase operation with no sent or disclosed target stage can replace its proof with a verified one for the same body.",
                AzoaErrorCodes.Conflict));
        return Ok(AZOAResult<WttOperationView>.Success((await lifecycle.InspectAsync(id, ct))!));
    }

    [HttpPost("operations/{id}/resume-proof")]
    public async Task<IActionResult> ResumeProof(string id, CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Unauthorized();
        logger.LogWarning("Operator {OperatorId} resumed WTT proof verification for {OperationId}", operatorActor.AuditActor, id);
        if (await lifecycle.InspectAsync(id, ct) is null) return NotFound();
        if (!await recovery.ResumeProofAsync(id, ct))
            return Conflict(AZOAResult<object>.FailureWithCode("Only a parked operation whose confirmed source transfer now has an attributed "
                + "publication, or whose final publication lacks a verified proof, can resume.", AzoaErrorCodes.Conflict));
        await recovery.ReconcileAsync(id, ct);
        return Ok(AZOAResult<WttOperationView>.Success((await lifecycle.InspectAsync(id, ct))!));
    }
}
