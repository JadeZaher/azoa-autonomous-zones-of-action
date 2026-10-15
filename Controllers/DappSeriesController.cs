using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/dapp-series")]
[Authorize]
public class DappSeriesController : ControllerBase
{
    private readonly IDappCompositionManager _manager;

    public DappSeriesController(IDappCompositionManager manager) => _manager = manager;

    // ── Series CRUD ──────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<DappSeries>>>> List(
        [FromQuery] DappSeries.StatusKind? status, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<IEnumerable<DappSeries>>("Invalid token."));
        var result = await _manager.ListAsync(userId.Value, status, ct);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<DappSeries>>> Get(Guid id, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<DappSeries>("Invalid token."));
        var result = await _manager.GetAsync(id, userId.Value, ct);
        if (result.IsError || result.Result is null) return NotFound(result);
        return Ok(result);
    }

    [HttpPost]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<DappSeries>>> Create(
        [FromBody] DappSeriesCreateModel model, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<DappSeries>("Invalid token."));
        var result = await _manager.CreateAsync(userId.Value, model, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<DappSeries>>> Update(
        Guid id, [FromBody] DappSeriesUpdateModel model, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<DappSeries>("Invalid token."));
        var result = await _manager.UpdateAsync(id, userId.Value, model, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResponse>> Delete(Guid id, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(new AZOAResponse { IsError = true, Message = "Invalid token." });
        var result = await _manager.DeleteAsync(id, userId.Value, ct);
        if (result.IsError || !result.Result) return BadRequest(new AZOAResponse { IsError = true, Message = result.Message });
        return Ok(new AZOAResponse { Message = "DappSeries deleted." });
    }

    // ── Series Action Management ────────────────────────────────────────────────────────

    [HttpGet("{seriesId:guid}/actions")]
    public async Task<ActionResult<AZOAResult<IEnumerable<DappSeriesAction>>>> ListActions(
        Guid seriesId, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<IEnumerable<DappSeriesAction>>("Invalid token."));
        var result = await _manager.ListActionsAsync(seriesId, userId.Value, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{seriesId:guid}/actions")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<DappSeriesAction>>> AddAction(
        Guid seriesId, [FromBody] DappSeriesAddActionModel model, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<DappSeriesAction>("Invalid token."));
        var result = await _manager.AddActionAsync(seriesId, userId.Value, model, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{seriesId:guid}/actions/{actionId:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResponse>> RemoveAction(Guid seriesId, Guid actionId, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(new AZOAResponse { IsError = true, Message = "Invalid token." });
        var result = await _manager.RemoveActionAsync(seriesId, userId.Value, actionId, ct);
        if (result.IsError || !result.Result) return BadRequest(new AZOAResponse { IsError = true, Message = result.Message });
        return Ok(new AZOAResponse { Message = "Action removed from series." });
    }

    [HttpPut("{seriesId:guid}/actions/{actionId:guid}/order")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<DappSeriesAction>>> ReorderAction(
        Guid seriesId, Guid actionId, [FromBody] DappSeriesReorderActionModel model, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<DappSeriesAction>("Invalid token."));
        var result = await _manager.ReorderActionAsync(seriesId, userId.Value, actionId, model.NewOrder, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{seriesId:guid}/actions/{actionId:guid}/mappings")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<DappSeriesAction>>> UpdateMappings(
        Guid seriesId, Guid actionId, [FromBody] DappSeriesUpdateMappingsModel model, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized(Fail<DappSeriesAction>("Invalid token."));
        var result = await _manager.UpdateMappingsAsync(seriesId, userId.Value, actionId, model.InputMappings, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
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
