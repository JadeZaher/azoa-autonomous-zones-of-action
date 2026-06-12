using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AvatarController : ControllerBase
{
    private readonly IAvatarManager _manager;

    public AvatarController(IAvatarManager manager)
    {
        _manager = manager;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<OASISResult<IAvatar>>> Register([FromBody] AvatarRegisterModel model, [FromQuery] OASISRequest? request)
    {
        var result = await _manager.RegisterAsync(model, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<OASISResult<string>>> Login([FromBody] AvatarLoginModel model, [FromQuery] OASISRequest? request)
    {
        var result = await _manager.LoginAsync(model, request);
        if (result.IsError) return Unauthorized(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<ActionResult<OASISResult<IAvatar>>> Get(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _manager.GetAsync(id, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet]
    [Authorize]
    public async Task<ActionResult<OASISResult<IEnumerable<IAvatar>>>> GetAll([FromQuery] OASISRequest? request)
    {
        var result = await _manager.GetAllAsync(request);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    [Authorize]
    public async Task<ActionResult<OASISResult<IAvatar>>> Update(Guid id, [FromBody] AvatarUpdateModel model, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.UpdateAsync(id, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [Authorize]
    public async Task<ActionResult<OASISResponse>> Delete(Guid id, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.DeleteAsync(id, avatarId.Value, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new OASISResponse { Message = "Avatar deleted." });
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
