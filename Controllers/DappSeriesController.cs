using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OASIS.WebAPI.Generated.SurrealDb;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Controllers;

[ApiController]
[Route("api/dapp-series")]
[Authorize]
public class DappSeriesController : ControllerBase
{
    private readonly IDappCompositionManager _manager;

    public DappSeriesController(IDappCompositionManager manager) => _manager = manager;

    // ── Series CRUD ──────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<ActionResult<OASISResult<IEnumerable<DappSeries>>>> List(
        [FromQuery] DappSeries.StatusKind? status, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<IEnumerable<DappSeries>>("Invalid token."));
        var result = await _manager.ListAsync(avatarId.Value, status, ct);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OASISResult<DappSeries>>> Get(Guid id, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappSeries>("Invalid token."));
        var result = await _manager.GetAsync(id, avatarId.Value, ct);
        if (result.IsError || result.Result is null) return NotFound(result);
        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<OASISResult<DappSeries>>> Create(
        [FromBody] DappSeriesCreateModel model, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappSeries>("Invalid token."));
        var result = await _manager.CreateAsync(avatarId.Value, model, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<OASISResult<DappSeries>>> Update(
        Guid id, [FromBody] DappSeriesUpdateModel model, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappSeries>("Invalid token."));
        var result = await _manager.UpdateAsync(id, avatarId.Value, model, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<OASISResponse>> Delete(Guid id, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(new OASISResponse { IsError = true, Message = "Invalid token." });
        var result = await _manager.DeleteAsync(id, avatarId.Value, ct);
        if (result.IsError || !result.Result) return BadRequest(new OASISResponse { IsError = true, Message = result.Message });
        return Ok(new OASISResponse { Message = "DappSeries deleted." });
    }

    // ── Series Quest Management ──────────────────────────────────────────────

    [HttpGet("{seriesId:guid}/quests")]
    public async Task<ActionResult<OASISResult<IEnumerable<DappSeriesQuest>>>> ListQuests(
        Guid seriesId, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<IEnumerable<DappSeriesQuest>>("Invalid token."));
        var result = await _manager.ListQuestsAsync(seriesId, avatarId.Value, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{seriesId:guid}/quests")]
    public async Task<ActionResult<OASISResult<DappSeriesQuest>>> AddQuest(
        Guid seriesId, [FromBody] DappSeriesAddQuestModel model, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappSeriesQuest>("Invalid token."));
        var result = await _manager.AddQuestAsync(seriesId, avatarId.Value, model, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{seriesId:guid}/quests/{questId:guid}")]
    public async Task<ActionResult<OASISResponse>> RemoveQuest(Guid seriesId, Guid questId, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(new OASISResponse { IsError = true, Message = "Invalid token." });
        var result = await _manager.RemoveQuestAsync(seriesId, avatarId.Value, questId, ct);
        if (result.IsError || !result.Result) return BadRequest(new OASISResponse { IsError = true, Message = result.Message });
        return Ok(new OASISResponse { Message = "Quest removed from series." });
    }

    [HttpPut("{seriesId:guid}/quests/{questId:guid}/order")]
    public async Task<ActionResult<OASISResult<DappSeriesQuest>>> ReorderQuest(
        Guid seriesId, Guid questId, [FromBody] DappSeriesReorderQuestModel model, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized(Fail<DappSeriesQuest>("Invalid token."));
        var result = await _manager.ReorderQuestAsync(seriesId, avatarId.Value, questId, model.NewOrder, ct);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{seriesId:guid}/quests/{questId:guid}/mappings")]
    public async Task<ActionResult<OASISResult<DappSeriesQuest>>> UpdateMappings(
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

    private static OASISResult<T> Fail<T>(string message) =>
        new() { IsError = true, Message = message };
}
