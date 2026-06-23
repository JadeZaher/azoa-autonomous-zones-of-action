using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

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
    public async Task<ActionResult<AZOAResult<IAvatar>>> Register([FromBody] AvatarRegisterModel model, [FromQuery] AZOARequest? request)
    {
        var result = await _manager.RegisterAsync(model, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AZOAResult<string>>> Login([FromBody] AvatarLoginModel model, [FromQuery] AZOARequest? request)
    {
        var result = await _manager.LoginAsync(model, request);
        if (result.IsError) return Unauthorized(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<ActionResult<AZOAResult<IAvatar>>> Get(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _manager.GetAsync(id, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet]
    [Authorize]
    public async Task<ActionResult<AZOAResult<IEnumerable<IAvatar>>>> GetAll([FromQuery] AZOARequest? request)
    {
        var result = await _manager.GetAllAsync(request);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    [Authorize]
    public async Task<ActionResult<AZOAResult<IAvatar>>> Update(Guid id, [FromBody] AvatarUpdateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.UpdateAsync(id, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [Authorize]
    public async Task<ActionResult<AZOAResponse>> Delete(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.DeleteAsync(id, avatarId.Value, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new AZOAResponse { Message = "Avatar deleted." });
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
