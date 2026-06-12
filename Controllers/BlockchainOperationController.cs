using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Controllers;

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
    public async Task<ActionResult<OASISResult<IBlockchainOperation>>> Get(Guid id, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.GetAsync(id, avatarId.Value, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("avatar/{avatarId:guid}")]
    public async Task<ActionResult<OASISResult<IEnumerable<IBlockchainOperation>>>> GetByAvatar(Guid avatarId, [FromQuery] OASISRequest? request)
    {
        var callerId = GetAvatarIdFromClaims();
        if (callerId == null) return Unauthorized();
        if (avatarId != callerId.Value)
            return StatusCode(StatusCodes.Status403Forbidden);

        var result = await _manager.GetByAvatarAsync(avatarId, request);
        return Ok(result);
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
