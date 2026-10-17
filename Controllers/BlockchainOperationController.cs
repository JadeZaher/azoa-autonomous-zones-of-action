using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.ClientSigning;
using AZOA.WebAPI.Services.Blockchain.ClientSigning;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BlockchainOperationController : ControllerBase
{
    private const int MaxStuckPage = 200;

    private readonly IBlockchainOperationManager _manager;
    private readonly ClientSignedOperationService _clientSigned;
    private readonly IReconciliationService _reconciliation;

    public BlockchainOperationController(IBlockchainOperationManager manager, ClientSignedOperationService clientSigned,
        IReconciliationService reconciliation)
    {
        _manager = manager;
        _clientSigned = clientSigned;
        _reconciliation = reconciliation;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<BlockchainOperationResponse>>> Get(Guid id, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _manager.GetAsync(id, userId.Value, request);
        if (result.IsError || result.Result == null)
        {
            return NotFound(AZOAResult<BlockchainOperationResponse>.FailureWithCode(
                "Operation not found.", AzoaErrorCodes.NotFound));
        }

        return Ok(AZOAResult<BlockchainOperationResponse>.Success(
            BlockchainOperationResponse.From(result.Result), result.Message));
    }

    [HttpGet("user/{userId:guid}")]
    public async Task<ActionResult<AZOAResult<IEnumerable<BlockchainOperationResponse>>>> GetByUser(Guid userId, [FromQuery] AZOARequest? request)
    {
        var callerId = GetUserIdFromClaims();
        if (callerId == null) return Unauthorized();
        if (userId != callerId.Value)
            return StatusCode(StatusCodes.Status403Forbidden);

        var result = await _manager.GetByUserAsync(userId, request);
        if (result.IsError || result.Result is null)
        {
            return Ok(AZOAResult<IEnumerable<BlockchainOperationResponse>>.FailureWithCode(
                "Operation history is unavailable.", AzoaErrorCodes.DependencyUnavailable));
        }

        return Ok(AZOAResult<IEnumerable<BlockchainOperationResponse>>.Success(
            result.Result.Select(BlockchainOperationResponse.From), result.Message));
    }

    /// <summary>D-3: the exact unsigned bytes the caller's External wallet must sign for this operation.</summary>
    [HttpGet("~/api/blockchain/operations/{id:guid}/signing-plan")]
    public async Task<IActionResult> GetSigningPlan(Guid id, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();
        var plan = await _clientSigned.GetPlanAsync(id, userId.Value, User.GetActingTenantId(), ct);
        Response.Headers.CacheControl = "no-store";
        return plan.IsError ? NotFound(plan) : Ok(plan);
    }

    /// <summary>
    /// D-3: submits the client-signed bytes. Only the saved plan signed by its signer is accepted; it is persisted before the
    /// broadcast and then reconciled from chain truth.
    /// </summary>
    [HttpPost("~/api/blockchain/operations/{id:guid}/submit-signed")]
    [EnableRateLimiting("financial")]
    public async Task<IActionResult> SubmitSigned(Guid id, [FromBody] ClientSignedSubmission submission, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();
        if (!User.HasSigningScope(AzoaScopes.TransferSign)) return Forbid();
        var submitted = await _clientSigned.SubmitAsync(id, userId.Value, User.GetActingTenantId(),
            submission?.SignedTransactionBase64 ?? string.Empty, ct);
        if (submitted.IsError) return Refusal(BlockchainOperationResponse.Project(submitted));
        await _reconciliation.ReconcileOperationAsync(id, ct);
        var current = await _manager.GetAsync(id, userId.Value);
        Response.Headers.CacheControl = "no-store";
        return Ok(BlockchainOperationResponse.Project(current.IsError ? submitted : current));
    }

    /// <summary>D-3 operator console: client-signed operations unsigned past the TTL or parked as NeedsOperator.</summary>
    [HttpGet("~/api/operator/blockchain-operations/stuck")]
    [Authorize(Policy = "RecentNodeOperatorSession")]
    public async Task<IActionResult> ListStuck([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(AZOAResult<IReadOnlyList<StuckClientSignedOperationView>>.Success(
            await _clientSigned.ListStuckAsync(Math.Clamp(limit, 1, MaxStuckPage), ct)));
    }

    /// <summary>D-3 operator resolution; the outcome is re-proven from chain state, never taken from the request.</summary>
    [HttpPost("~/api/operator/blockchain-operations/{id:guid}/resolve")]
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [EnableRateLimiting("financial")]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] ClientSigningResolution resolution, CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var operatorId)) return Unauthorized();
        var resolved = await _clientSigned.ResolveAsync(id, operatorId, resolution, ct);
        Response.Headers.CacheControl = "no-store";
        return resolved.IsError ? Refusal(BlockchainOperationResponse.Project(resolved)) : Ok(BlockchainOperationResponse.Project(resolved));
    }

    private IActionResult Refusal(AZOAResult<BlockchainOperationResponse> refusal) => refusal.Code switch
    {
        AzoaErrorCodes.NotFound => NotFound(refusal),
        AzoaErrorCodes.Conflict => Conflict(refusal),
        AzoaErrorCodes.DependencyUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, refusal),
        ClientSigningCodes.SignatureMismatch => UnprocessableEntity(refusal),
        _ => BadRequest(refusal),
    };

    private Guid? GetUserIdFromClaims()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
