using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class QuestController : ControllerBase
{
    private readonly IQuestManager _questManager;

    public QuestController(IQuestManager questManager)
    {
        _questManager = questManager;
    }

    // ─── Quest CRUD ───

    [HttpPost]
    public async Task<ActionResult<OASISResult<Quest>>> Create([FromBody] QuestCreateModel model, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.CreateAsync(model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OASISResult<Quest>>> Get(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _questManager.GetAsync(id, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("avatar/{avatarId:guid}")]
    public async Task<ActionResult<OASISResult<IEnumerable<Quest>>>> GetByAvatar(Guid avatarId, [FromQuery] OASISRequest? request)
    {
        var result = await _questManager.GetByAvatarAsync(avatarId, request);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<OASISResult<Quest>>> Update(Guid id, [FromBody] QuestUpdateModel model, [FromQuery] OASISRequest? request)
    {
        var result = await _questManager.UpdateAsync(id, model, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<OASISResponse>> Delete(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _questManager.DeleteAsync(id, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new OASISResponse { Message = "Quest deleted." });
    }

    // ─── DAG validation ───

    [HttpPost("{id:guid}/validate")]
    public async Task<ActionResult<OASISResult<bool>>> Validate(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _questManager.ValidateDAGAsync(id, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Execution ───

    [HttpPost("{id:guid}/execute")]
    public async Task<ActionResult<OASISResult<QuestRun>>> Execute(Guid id, [FromQuery] OASISRequest? request)
    {
        // Returns the produced QuestRun (one execution attempt). Runtime state
        // — per-node State/Output/Error — lives on the per-(run, node)
        // QuestNodeExecution rows (queryable separately via the run id).
        var result = await _questManager.ExecuteAsync(id, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/nodes/{nodeId:guid}/execute")]
    public async Task<ActionResult<OASISResult<QuestNodeExecution>>> ExecuteNode(Guid id, Guid nodeId, [FromQuery] OASISRequest? request)
    {
        // Single-node execution produces an ad-hoc one-node QuestRun and
        // returns the QuestNodeExecution row for the result.
        var result = await _questManager.ExecuteNodeAsync(id, nodeId, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("runs/{runId:guid}/fork")]
    public async Task<ActionResult<OASISResult<QuestRun>>> Fork(Guid runId, [FromBody] QuestForkRequest body, [FromQuery] OASISRequest? request)
    {
        var result = await _questManager.ForkAsync(runId, body.AtNodeId, body.Reason, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("runs/{runId:guid}/mark-failed")]
    public async Task<ActionResult<OASISResult<QuestRun>>> MarkRunFailed(Guid runId, [FromBody] QuestMarkFailedRequest body, [FromQuery] OASISRequest? request)
    {
        var result = await _questManager.MarkRunFailedAsync(runId, body.Reason, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Templates ───

    [HttpPost("templates")]
    public async Task<ActionResult<OASISResult<QuestTemplate>>> CreateTemplate([FromBody] QuestTemplateCreateModel model, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<QuestTemplate> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.CreateTemplateAsync(model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("templates/{id:guid}")]
    public async Task<ActionResult<OASISResult<QuestTemplate>>> GetTemplate(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _questManager.GetTemplateAsync(id, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("templates")]
    public async Task<ActionResult<OASISResult<IEnumerable<QuestTemplate>>>> ListTemplates([FromQuery] OASISRequest? request)
    {
        var result = await _questManager.ListTemplatesAsync(request);
        return Ok(result);
    }

    [HttpPost("templates/{id:guid}/instantiate")]
    public async Task<ActionResult<OASISResult<Quest>>> InstantiateTemplate(Guid id, [FromBody] Dictionary<string, string>? parameters, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.InstantiateTemplateAsync(id, avatarId.Value, parameters, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Node Templates ───

    [HttpPost("node-templates")]
    public async Task<ActionResult<OASISResult<QuestNodeTemplate>>> CreateNodeTemplate([FromBody] QuestNodeTemplateCreateModel model, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<QuestNodeTemplate> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.CreateNodeTemplateAsync(model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("node-templates")]
    public async Task<ActionResult<OASISResult<IEnumerable<QuestNodeTemplate>>>> ListNodeTemplates([FromQuery] OASISRequest? request)
    {
        var result = await _questManager.ListNodeTemplatesAsync(request);
        return Ok(result);
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
