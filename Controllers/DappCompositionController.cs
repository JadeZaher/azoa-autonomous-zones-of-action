using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Composition + action graph bundle generation/deployment endpoints for the
/// <c>dapp-composition</c> aggregate. Series CRUD + action management live on
/// <see cref="DappSeriesController"/>; this controller is the pipeline:
/// validate -> compose -> generate -> deploy.
/// </summary>
[ApiController]
[Route("api/dapp-series/{id:guid}")]
[Authorize]
public class DappCompositionController : ControllerBase
{
    private readonly IDappCompositionManager _manager;

    private readonly AZOA.WebAPI.Services.Signing.SigningAdmissionAuthorityFactory? _signingAuthority;

    public DappCompositionController(IDappCompositionManager manager,
        AZOA.WebAPI.Services.Signing.SigningAdmissionAuthorityFactory? signingAuthority = null)
    {
        _manager = manager;
        _signingAuthority = signingAuthority;
    }

    [HttpPost("compose")]
    [Authorize(Policy = "DappManage")]
    public async Task<ActionResult<AZOAResult<DappManifest>>> Compose(Guid id, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<DappManifest>("Invalid token."));
        var result = await _manager.ComposeAsync(id, userId.Value, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("validate")]
    public async Task<ActionResult<AZOAResult<CompositionValidationResult>>> Validate(Guid id, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<CompositionValidationResult>("Invalid token."));
        var result = await _manager.ValidateAsync(id, userId.Value, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("manifest")]
    public async Task<ActionResult<AZOAResult<DappManifest>>> GetManifest(Guid id, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<DappManifest>("Invalid token."));
        var seriesLoad = await _manager.GetAsync(id, userId.Value, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null) return NotFound(Fail<DappManifest>(seriesLoad.Message));
        if (string.IsNullOrEmpty(seriesLoad.Result.Manifest))
            return NotFound(Fail<DappManifest>("Series has no composed manifest yet; call /compose first."));
        try
        {
            var manifest = System.Text.Json.JsonSerializer.Deserialize<DappManifest>(seriesLoad.Result.Manifest!);
            return Ok(new AZOAResult<DappManifest> { Result = manifest, Message = "Success" });
        }
        catch (System.Text.Json.JsonException ex)
        {
            return BadRequest(Fail<DappManifest>($"Stored manifest is malformed: {ex.Message}"));
        }
    }

    [HttpPost("generate")]
    [Authorize(Policy = "DappManage")]
    public async Task<ActionResult<AZOAResult<IActionGraphBundle>>> Generate(Guid id, [FromBody] DeploymentGenerateRequest request, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<IActionGraphBundle>("Invalid token."));
        var result = await _manager.GenerateAsync(id, userId.Value, ct, request, ActingTenantId());
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("deploy")]
    [Authorize(Policy = "DappManage")]
    public async Task<ActionResult<AZOAResult<DeploymentReceipt>>> Deploy(
        Guid id, [FromBody] DeploymentStartRequest request, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<DeploymentReceipt>("Invalid token."));
        // Audit-1 H1: native signers bind to the caller's captured credential; none ⇒ native signing fails closed.
        var credential = _signingAuthority is null ? null : await _signingAuthority.CaptureCredentialAsync(User, ct);
        var result = await _manager.DeployAsync(id, userId.Value, request, Request.Headers["Idempotency-Key"].ToString(), ActingTenantId(), ct,
            credential is { IsError: false } ? credential.Result : null);
        if (result.IsError) return BadRequest(result);
        return result.Result?.State is "Confirmed" or "Failed" or "Cancelled" ? Ok(result) : StatusCode(202, result);
    }

    private Guid? ActingTenantId() => Guid.TryParse(User.FindFirst("act_as_tenant")?.Value, out var id) ? id : null;

    [HttpGet("status")]
    public async Task<ActionResult<AZOAResult<DappSeries.StatusKind>>> GetStatus(Guid id, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<DappSeries.StatusKind>("Invalid token."));
        var seriesLoad = await _manager.GetAsync(id, userId.Value, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null) return NotFound(Fail<DappSeries.StatusKind>(seriesLoad.Message));
        return Ok(new AZOAResult<DappSeries.StatusKind> { Result = seriesLoad.Result.Status, Message = "Success" });
    }

    private Guid? GetUserIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private static AZOAResult<T> Fail<T>(string message) =>
        new() { IsError = true, Message = message };
}
