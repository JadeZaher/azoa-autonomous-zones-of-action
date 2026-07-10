using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
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
    public async Task<ActionResult<AZOAResult<object>>> Get(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _manager.GetAsync(id, request);
        if (result.IsError || result.Result == null)
        {
            return NotFound(new AZOAResult<object> { IsError = result.IsError, Message = result.Message });
        }

        var callerId = GetAvatarIdFromClaims();
        var isSelf = callerId.HasValue && callerId.Value == id;
        var projected = isSelf ? (object)result.Result : PublicAvatarInfo.From(result.Result);

        return Ok(new AZOAResult<object> { IsError = false, Message = result.Message, Result = projected });
    }

    // Marketplace directory: public projections only (no PII). Full records are
    // only ever available via self-get above.
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<AZOAResult<IEnumerable<PublicAvatarInfo>>>> GetAll([FromQuery] AZOARequest? request)
    {
        var result = await _manager.GetAllAsync(request);
        if (result.IsError || result.Result == null)
        {
            return Ok(new AZOAResult<IEnumerable<PublicAvatarInfo>> { IsError = result.IsError, Message = result.Message });
        }

        var projected = result.Result.Select(PublicAvatarInfo.From);
        return Ok(new AZOAResult<IEnumerable<PublicAvatarInfo>> { IsError = false, Message = result.Message, Result = projected });
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

    /// <summary>
    /// Server-side "logout everywhere": invalidates every live JWT for the
    /// authenticated avatar by bumping its AuthNotBefore watermark. The subject is
    /// taken from the token (never the body) per the IDOR rule.
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> LogoutEverywhere()
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.LogoutEverywhereAsync(avatarId.Value, HttpContext.RequestAborted);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    /// <summary>
    /// avatar-dapp-rbac: assign an avatar's DApp role. The target id is the ROUTE id
    /// (never the body). Authority: an operator (JWT-only operator:admin) may assign any
    /// role incl. manager (the operator-bootstrap path); a DApp manager may assign only
    /// developer/user. Ordinary users and developers are denied by the manager.
    /// operator:admin can never be assigned — DappRole only ranges over the
    /// AzoaDappRoles allowlist. See Controllers/AGENTS.md §avatar-dapp-rbac.
    /// </summary>
    [HttpPut("{id:guid}/dapp-role")]
    [Authorize]
    public async Task<ActionResult<AZOAResult<IAvatar>>> AssignDappRole(
        Guid id, [FromBody] AvatarRoleAssignmentModel model)
    {
        // Manager authority is the CURRENT dapp_role signal (the real ApiKey handler
        // stamps this from the owner's live store role), NOT scope-or-role — so a
        // stale-scope key whose owner was demoted cannot grant roles.
        var result = await _manager.AssignDappRoleAsync(
            id, model.Role, ActingIsOperator(), User.HasDappManagerRole(), HttpContext.RequestAborted);

        if (result.IsError)
            return result.Message?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true
                ? NotFound(result)
                : StatusCode(StatusCodes.Status403Forbidden, result);

        return Ok(result);
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    /// <summary>
    /// Mirrors the "Operator" authorization policy (Program.cs): operator authority is
    /// JWT-only (an API-key principal is rejected outright) AND requires an explicit
    /// admin signal. Kept in lock-step with that policy — see Controllers/AGENTS.md.
    /// </summary>
    private bool ActingIsOperator()
    {
        if (string.Equals(User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase))
            return false;

        return User.HasScope(AzoaScopes.Operator)
            || User.IsInRole("Admin")
            || string.Equals(User.FindFirst("role")?.Value, "Admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(User.FindFirst("is_admin")?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
