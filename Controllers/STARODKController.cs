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
[Authorize]
public class STARODKController : ControllerBase
{
    private readonly ISTARManager _manager;

    public STARODKController(ISTARManager manager)
    {
        _manager = manager;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<ISTARODK>>> Get(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _manager.GetAsync(id, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<ISTARODK>>>> GetAll([FromQuery] AZOARequest? request)
    {
        var result = await _manager.GetAllAsync(request);
        return Ok(result);
    }

    /// <summary>Creates a new STARODK owned by the authenticated avatar, or
    /// upserts an existing record they already own (by name).</summary>
    [HttpPost]
    public async Task<ActionResult<AZOAResult<ISTARODK>>> CreateOrUpdate([FromBody] STARODKCreateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.CreateOrUpdateAsync(model, avatarId.Value, routeId: null, request);
        return TranslateUpsertResult(result);
    }

    /// <summary>Updates an existing STARODK by route id. Verifies the record is
    /// owned by the authenticated avatar before writing — closes the PUT IDOR.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AZOAResult<ISTARODK>>> Update(Guid id, [FromBody] STARODKCreateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.CreateOrUpdateAsync(model, avatarId.Value, routeId: id, request);
        return TranslateUpsertResult(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<AZOAResponse>> Delete(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.DeleteAsync(id, avatarId.Value, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new AZOAResponse { Message = "STAR ODK deleted." });
    }

    [HttpPost("{id:guid}/generate")]
    public async Task<ActionResult<AZOAResult<ISTARODK>>> Generate(Guid id, [FromBody] STARDappGenerationRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.GenerateAsync(id, request, avatarId.Value, providerRequest);
        return TranslateUpsertResult(result);
    }

    [HttpPost("{id:guid}/deploy")]
    public async Task<ActionResult<AZOAResult<ISTARODK>>> Deploy(Guid id, [FromQuery] AZOARequest? providerRequest)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.DeployAsync(id, avatarId.Value, providerRequest);
        return TranslateUpsertResult(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Translates a manager upsert result to the right HTTP status. The manager
    /// flags authorisation failures with the <see cref="STARODKAuthorizationError"/>
    /// prefixes so the controller can return 403/404 instead of 400 for them.
    /// </summary>
    private ActionResult<AZOAResult<ISTARODK>> TranslateUpsertResult(AZOAResult<ISTARODK> result)
    {
        if (!result.IsError) return Ok(result);

        if (result.Message?.StartsWith(STARODKAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
            return StatusCode(StatusCodes.Status403Forbidden, result);

        if (result.Message?.StartsWith(STARODKAuthorizationError.NotFound, StringComparison.Ordinal) == true)
            return NotFound(result);

        return BadRequest(result);
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
