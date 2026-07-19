using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BlockchainOperationController : ControllerBase
{
    private readonly IBlockchainOperationManager _manager;

    public BlockchainOperationController(IBlockchainOperationManager manager)
    {
        _manager = manager;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<BlockchainOperationResponse>>> Get(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.GetAsync(id, avatarId.Value, request);
        if (result.IsError || result.Result == null)
        {
            return NotFound(AZOAResult<BlockchainOperationResponse>.FailureWithCode(
                "Operation not found.", AzoaErrorCodes.NotFound));
        }

        return Ok(AZOAResult<BlockchainOperationResponse>.Success(
            BlockchainOperationResponse.From(result.Result), result.Message));
    }

    [HttpGet("avatar/{avatarId:guid}")]
    public async Task<ActionResult<AZOAResult<IEnumerable<BlockchainOperationResponse>>>> GetByAvatar(Guid avatarId, [FromQuery] AZOARequest? request)
    {
        var callerId = GetAvatarIdFromClaims();
        if (callerId == null) return Unauthorized();
        if (avatarId != callerId.Value)
            return StatusCode(StatusCodes.Status403Forbidden);

        var result = await _manager.GetByAvatarAsync(avatarId, request);
        if (result.IsError || result.Result is null)
        {
            return Ok(AZOAResult<IEnumerable<BlockchainOperationResponse>>.FailureWithCode(
                "Operation history is unavailable.", AzoaErrorCodes.DependencyUnavailable));
        }

        return Ok(AZOAResult<IEnumerable<BlockchainOperationResponse>>.Success(
            result.Result.Select(BlockchainOperationResponse.From), result.Message));
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
