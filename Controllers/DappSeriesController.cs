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
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<IEnumerable<DappSeries>>("Invalid token."));
        var result = await _manager.ListAsync(avatarId.Value, status, ct);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<DappSeries>>> Get(Guid id, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappSeries>("Invalid token."));
        var result = await _manager.GetAsync(id, avatarId.Value, ct);
        if (result.IsError || result.Result is null) return NotFound(result);
        return Ok(result);
    }

    [HttpPost]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<DappSeries>>> Create(
        [FromBody] DappSeriesCreateModel model, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappSeries>("Invalid token."));
        var result = await _manager.CreateAsync(avatarId.Value, model, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<DappSeries>>> Update(
        Guid id, [FromBody] DappSeriesUpdateModel model, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappSeries>("Invalid token."));
        var result = await _manager.UpdateAsync(id, avatarId.Value, model, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResponse>> Delete(Guid id, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(new AZOAResponse { IsError = true, Message = "Invalid token." });
        var result = await _manager.DeleteAsync(id, avatarId.Value, ct);
        if (result.IsError || !result.Result) return BadRequest(new AZOAResponse { IsError = true, Message = result.Message });
        return Ok(new AZOAResponse { Message = "DappSeries deleted." });
    }

    // ── Series Quest Management ──────────────────────────────────────────────

    [HttpGet("{seriesId:guid}/quests")]
    public async Task<ActionResult<AZOAResult<IEnumerable<DappSeriesQuest>>>> ListQuests(
        Guid seriesId, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<IEnumerable<DappSeriesQuest>>("Invalid token."));
        var result = await _manager.ListQuestsAsync(seriesId, avatarId.Value, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{seriesId:guid}/quests")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<DappSeriesQuest>>> AddQuest(
        Guid seriesId, [FromBody] DappSeriesAddQuestModel model, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappSeriesQuest>("Invalid token."));
        var result = await _manager.AddQuestAsync(seriesId, avatarId.Value, model, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{seriesId:guid}/quests/{questId:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResponse>> RemoveQuest(Guid seriesId, Guid questId, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(new AZOAResponse { IsError = true, Message = "Invalid token." });
        var result = await _manager.RemoveQuestAsync(seriesId, avatarId.Value, questId, ct);
        if (result.IsError || !result.Result) return BadRequest(new AZOAResponse { IsError = true, Message = result.Message });
        return Ok(new AZOAResponse { Message = "Quest removed from series." });
    }

    [HttpPut("{seriesId:guid}/quests/{questId:guid}/order")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<DappSeriesQuest>>> ReorderQuest(
        Guid seriesId, Guid questId, [FromBody] DappSeriesReorderQuestModel model, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappSeriesQuest>("Invalid token."));
        var result = await _manager.ReorderQuestAsync(seriesId, avatarId.Value, questId, model.NewOrder, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{seriesId:guid}/quests/{questId:guid}/mappings")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<DappSeriesQuest>>> UpdateMappings(
        Guid seriesId, Guid questId, [FromBody] DappSeriesUpdateMappingsModel model, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappSeriesQuest>("Invalid token."));
        var result = await _manager.UpdateMappingsAsync(seriesId, avatarId.Value, questId, model.InputMappings, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private static AZOAResult<T> Fail<T>(string message) =>
        new() { IsError = true, Message = message };
}
