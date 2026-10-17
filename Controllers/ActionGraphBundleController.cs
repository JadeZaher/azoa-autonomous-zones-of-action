using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Ecosystem;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ActionGraphBundleController : ControllerBase
{
    private readonly IActionGraphBundleManager _manager;

    public ActionGraphBundleController(IActionGraphBundleManager manager)
    {
        _manager = manager;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<IActionGraphBundle>>> Get(Guid id, [FromQuery] AZOARequest? request)
    {
        // Owner-or-public read scope: a non-owner cannot read another tenant's private
        // action graph bundle by id (manager returns "not found" rather than confirming it).
        var result = await _manager.GetAsync(id, GetUserIdFromClaims(), request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<IActionGraphBundle>>>> GetAll([FromQuery] AZOARequest? request)
    {
        var result = await _manager.GetAllAsync(GetUserIdFromClaims(), request);
        return Ok(result);
    }

    /// <summary>Creates a new ActionGraphBundle owned by the authenticated user, or
    /// upserts an existing record they already own (by name).</summary>
    [HttpPost]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<IActionGraphBundle>>> CreateOrUpdate([FromBody] ActionGraphBundleCreateModel model, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _manager.CreateOrUpdateAsync(model, userId.Value, routeId: null, request);
        return TranslateUpsertResult(result);
    }

    /// <summary>Updates an existing ActionGraphBundle by route id. Verifies the record is
    /// owned by the authenticated user before writing — closes the PUT IDOR.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<IActionGraphBundle>>> Update(Guid id, [FromBody] ActionGraphBundleCreateModel model, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _manager.CreateOrUpdateAsync(model, userId.Value, routeId: id, request);
        return TranslateUpsertResult(result);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResponse>> Delete(Guid id, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _manager.DeleteAsync(id, userId.Value, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new AZOAResponse { Message = "action graph bundle deleted." });
    }

    [HttpPost("{id:guid}/generate")]
    [Authorize(Policy = "DappManage")]
    public async Task<ActionResult<AZOAResult<IActionGraphBundle>>> Generate(Guid id, [FromBody] ActionGraphBundleDappGenerationRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _manager.GenerateAsync(id, request, userId.Value, providerRequest, ActingTenantId());
        return TranslateUpsertResult(result);
    }

    [HttpPost("{id:guid}/deploy")]
    [Authorize(Policy = "DappManage")]
    public async Task<ActionResult<AZOAResult<DeploymentReceipt>>> Deploy(Guid id, [FromBody] DeploymentStartRequest request, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _manager.DeployAsync(id, request, Request.Headers["Idempotency-Key"].ToString(), userId.Value, ActingTenantId(), ct);
        if (result.IsError) return BadRequest(result);
        return result.Result?.State is "Confirmed" or "Failed" or "Cancelled" ? Ok(result) : StatusCode(202, result);
    }

    private Guid? ActingTenantId() => Guid.TryParse(User.FindFirst("act_as_tenant")?.Value, out var id) ? id : null;

    // ── Ecosystem tree (D2) ─────────────────────────────────────────────────

    /// <summary>Attaches a DappSeries (or nested ActionGraphBundle) as a node in this
    /// ActionGraphBundle's ecosystem tree and returns the recomposed tree. The owner is the
    /// authenticated user — no owner id is read from the body (IDOR-resistant).</summary>
    [HttpPost("{id:guid}/ecosystem/dapp-series")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<EcosystemTree>>> AddDappSeries(Guid id, [FromBody] AddDappSeriesRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _manager.AddDappSeriesAsync(id, request, userId.Value, providerRequest);
        return TranslateTreeResult(result);
    }

    /// <summary>Returns this ActionGraphBundle's ecosystem assembled into a parent/children
    /// tree. Ownership is verified against the authenticated user.</summary>
    [HttpGet("{id:guid}/ecosystem")]
    public async Task<ActionResult<AZOAResult<EcosystemTree>>> GetEcosystem(Guid id, [FromQuery] AZOARequest? providerRequest)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _manager.GetEcosystemAsync(id, userId.Value, providerRequest);
        return TranslateTreeResult(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Same auth-prefix translation as <see cref="TranslateUpsertResult"/>,
    /// for the <see cref="EcosystemTree"/> result shape.</summary>
    private ActionResult<AZOAResult<EcosystemTree>> TranslateTreeResult(AZOAResult<EcosystemTree> result)
    {
        if (!result.IsError) return Ok(result);

        if (result.Message?.StartsWith(ActionGraphBundleAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
            return StatusCode(StatusCodes.Status403Forbidden, result);

        if (result.Message?.StartsWith(ActionGraphBundleAuthorizationError.NotFound, StringComparison.Ordinal) == true)
            return NotFound(result);

        return BadRequest(result);
    }

    /// <summary>
    /// Translates a manager upsert result to the right HTTP status. The manager
    /// flags authorisation failures with the <see cref="ActionGraphBundleAuthorizationError"/>
    /// prefixes so the controller can return 403/404 instead of 400 for them.
    /// </summary>
    private ActionResult<AZOAResult<IActionGraphBundle>> TranslateUpsertResult(AZOAResult<IActionGraphBundle> result)
    {
        if (!result.IsError) return Ok(result);

        if (result.Message?.StartsWith(ActionGraphBundleAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
            return StatusCode(StatusCodes.Status403Forbidden, result);

        if (result.Message?.StartsWith(ActionGraphBundleAuthorizationError.NotFound, StringComparison.Ordinal) == true)
            return NotFound(result);

        return BadRequest(result);
    }

    private Guid? GetUserIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
