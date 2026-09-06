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
public class UserController : ControllerBase
{
    private readonly IUserManager _manager;

    public UserController(IUserManager manager)
    {
        _manager = manager;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<AZOAResult<IUser>>> Register([FromBody] UserRegisterModel model, [FromQuery] AZOARequest? request)
    {
        var result = await _manager.RegisterAsync(model, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AZOAResult<string>>> Login([FromBody] UserLoginModel model, [FromQuery] AZOARequest? request)
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

        var callerId = GetUserIdFromClaims();
        var isSelf = callerId.HasValue && callerId.Value == id;
        var projected = isSelf ? (object)result.Result : PublicUserInfo.From(result.Result);

        return Ok(new AZOAResult<object> { IsError = false, Message = result.Message, Result = projected });
    }

    // Marketplace directory: public projections only (no PII). Full records are
    // only ever available via self-get above.
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<AZOAResult<IEnumerable<PublicUserInfo>>>> GetAll([FromQuery] AZOARequest? request)
    {
        var result = await _manager.GetAllAsync(request);
        if (result.IsError || result.Result == null)
        {
            return Ok(new AZOAResult<IEnumerable<PublicUserInfo>> { IsError = result.IsError, Message = result.Message });
        }

        var projected = result.Result.Select(PublicUserInfo.From);
        return Ok(new AZOAResult<IEnumerable<PublicUserInfo>> { IsError = false, Message = result.Message, Result = projected });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "FirstPartyLogin")]
    public async Task<ActionResult<AZOAResult<IUser>>> Update(Guid id, [FromBody] UserUpdateModel model, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _manager.UpdateAsync(id, model, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "FirstPartyLogin")]
    public async Task<ActionResult<AZOAResponse>> Delete(Guid id, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _manager.DeleteAsync(id, userId.Value, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new AZOAResponse { Message = "User deleted." });
    }

    /// <summary>
    /// Server-side "logout everywhere": invalidates every live JWT for the
    /// authenticated user by bumping its AuthNotBefore watermark. The subject is
    /// taken from the token (never the body) per the IDOR rule.
    /// </summary>
    [HttpPost("logout")]
    [Authorize(Policy = "FirstPartyLogin")]
    public async Task<IActionResult> LogoutEverywhere()
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _manager.LogoutEverywhereAsync(userId.Value, HttpContext.RequestAborted);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    /// <summary>
    /// user-dapp-rbac: assign an user's DApp role. The target id is the ROUTE id
    /// (never the body). Authority: an operator (JWT-only operator:admin) may assign any
    /// role incl. manager (the operator-bootstrap path); a DApp manager may assign only
    /// developer/user. Ordinary users and developers are denied by the manager.
    /// operator:admin can never be assigned — DappRole only ranges over the
    /// AzoaDappRoles allowlist. See Controllers/AGENTS.md §user-dapp-rbac.
    /// </summary>
    [HttpPut("{id:guid}/dapp-role")]
    [Authorize(Policy = "DappRoleAssignment")]
    public async Task<ActionResult<AZOAResult<IUser>>> AssignDappRole(
        Guid id, [FromBody] UserRoleAssignmentModel model)
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

    private Guid? GetUserIdFromClaims()
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

        return AzoaClaims.IsNodeOperator(User)
            && User.HasScope(AzoaScopes.Operator)
            && GetUserIdFromClaims() == AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.UserId;
    }
}
