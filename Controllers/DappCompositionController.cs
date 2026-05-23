using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OASIS.WebAPI.Generated.SurrealDb;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Controllers;

/// <summary>
/// Composition + STAR generation/deployment endpoints for the
/// <c>dapp-composition</c> aggregate. Series CRUD + quest management live on
/// <see cref="DappSeriesController"/>; this controller is the pipeline:
/// validate -> compose -> generate -> deploy.
/// </summary>
[ApiController]
[Route("api/dapp-series/{id:guid}")]
[Authorize]
public class DappCompositionController : ControllerBase
{
    private readonly IDappCompositionManager _manager;

    public DappCompositionController(IDappCompositionManager manager) => _manager = manager;

    [HttpPost("compose")]
    public async Task<ActionResult<OASISResult<DappManifest>>> Compose(Guid id, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappManifest>("Invalid token."));
        var result = await _manager.ComposeAsync(id, avatarId.Value, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("validate")]
    public async Task<ActionResult<OASISResult<CompositionValidationResult>>> Validate(Guid id, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<CompositionValidationResult>("Invalid token."));
        var result = await _manager.ValidateAsync(id, avatarId.Value, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("manifest")]
    public async Task<ActionResult<OASISResult<DappManifest>>> GetManifest(Guid id, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappManifest>("Invalid token."));
        var seriesLoad = await _manager.GetAsync(id, avatarId.Value, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null) return NotFound(Fail<DappManifest>(seriesLoad.Message));
        if (string.IsNullOrEmpty(seriesLoad.Result.Manifest))
            return NotFound(Fail<DappManifest>("Series has no composed manifest yet; call /compose first."));
        try
        {
            var manifest = System.Text.Json.JsonSerializer.Deserialize<DappManifest>(seriesLoad.Result.Manifest!);
            return Ok(new OASISResult<DappManifest> { Result = manifest, Message = "Success" });
        }
        catch (System.Text.Json.JsonException ex)
        {
            return BadRequest(Fail<DappManifest>($"Stored manifest is malformed: {ex.Message}"));
        }
    }

    [HttpPost("generate")]
    public async Task<ActionResult<OASISResult<ISTARODK>>> Generate(Guid id, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<ISTARODK>("Invalid token."));
        var result = await _manager.GenerateAsync(id, avatarId.Value, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("deploy")]
    public async Task<ActionResult<OASISResult<ISTARODK>>> Deploy(
        Guid id, [FromQuery] string? targetOverride, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<ISTARODK>("Invalid token."));
        var result = await _manager.DeployAsync(id, avatarId.Value, targetOverride, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("status")]
    public async Task<ActionResult<OASISResult<DappSeries.StatusKind>>> GetStatus(Guid id, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappSeries.StatusKind>("Invalid token."));
        var seriesLoad = await _manager.GetAsync(id, avatarId.Value, ct);
        if (seriesLoad.IsError || seriesLoad.Result is null) return NotFound(Fail<DappSeries.StatusKind>(seriesLoad.Message));
        return Ok(new OASISResult<DappSeries.StatusKind> { Result = seriesLoad.Result.Status, Message = "Success" });
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private static OASISResult<T> Fail<T>(string message) =>
        new() { IsError = true, Message = message };
}
