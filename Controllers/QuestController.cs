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
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.GetAsync(id, avatarId.Value, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("avatar/{avatarId:guid}")]
    public async Task<ActionResult<OASISResult<IEnumerable<Quest>>>> GetByAvatar(Guid avatarId, [FromQuery] OASISRequest? request)
    {
        var callerId = GetAvatarIdFromClaims();
        if (callerId == null)
            return Unauthorized(new OASISResult<IEnumerable<Quest>> { IsError = true, Message = "Invalid token." });
        if (avatarId != callerId.Value)
            return StatusCode(StatusCodes.Status403Forbidden);

        var result = await _questManager.GetByAvatarAsync(avatarId, request);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<OASISResult<Quest>>> Update(Guid id, [FromBody] QuestUpdateModel model, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.UpdateAsync(id, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<OASISResponse>> Delete(Guid id, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.DeleteAsync(id, avatarId.Value, request);
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
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<QuestRun> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.ExecuteAsync(id, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/nodes/{nodeId:guid}/execute")]
    public async Task<ActionResult<OASISResult<QuestNodeExecution>>> ExecuteNode(Guid id, Guid nodeId, [FromQuery] OASISRequest? request)
    {
        // Single-node execution produces an ad-hoc one-node QuestRun and
        // returns the QuestNodeExecution row for the result.
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<QuestNodeExecution> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.ExecuteNodeAsync(id, nodeId, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("runs/{runId:guid}/fork")]
    public async Task<ActionResult<OASISResult<QuestRun>>> Fork(Guid runId, [FromBody] QuestForkRequest body, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<QuestRun> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.ForkAsync(runId, body.AtNodeId, body.Reason, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("runs/{runId:guid}/mark-failed")]
    public async Task<ActionResult<OASISResult<QuestRun>>> MarkRunFailed(Guid runId, [FromBody] QuestMarkFailedRequest body, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<QuestRun> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.MarkRunFailedAsync(runId, body.Reason, avatarId.Value, request);
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

    // ─── Quest Nodes sub-resource ───

    [HttpGet("{questId:guid}/nodes")]
    public async Task<ActionResult<OASISResult<IEnumerable<QuestNode>>>> ListNodes(Guid questId, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<IEnumerable<QuestNode>> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.ListNodesAsync(questId, avatarId.Value, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpPost("{questId:guid}/nodes")]
    public async Task<ActionResult<OASISResult<QuestNode>>> AddNode(Guid questId, [FromBody] QuestNodeCreateModel model, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<QuestNode> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.AddNodeAsync(questId, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{questId:guid}/nodes/{nodeId:guid}")]
    public async Task<ActionResult<OASISResult<QuestNode>>> UpdateNode(Guid questId, Guid nodeId, [FromBody] QuestNodeUpdateModel model, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<QuestNode> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.UpdateNodeAsync(questId, nodeId, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{questId:guid}/nodes/{nodeId:guid}")]
    public async Task<ActionResult<OASISResponse>> DeleteNode(Guid questId, Guid nodeId, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<bool> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.DeleteNodeAsync(questId, nodeId, avatarId.Value, request);
        if (result.IsError || !result.Result) return BadRequest(result);
        return Ok(new OASISResponse { Message = "Node deleted." });
    }

    // ─── Quest Edges sub-resource ───

    [HttpPost("{questId:guid}/edges")]
    public async Task<ActionResult<OASISResult<QuestEdge>>> AddEdge(Guid questId, [FromBody] QuestEdgeAddModel model, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<QuestEdge> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.AddEdgeAsync(questId, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{questId:guid}/edges/{edgeId:guid}")]
    public async Task<ActionResult<OASISResponse>> RemoveEdge(Guid questId, Guid edgeId, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<bool> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.RemoveEdgeAsync(questId, edgeId, avatarId.Value, request);
        if (result.IsError || !result.Result) return BadRequest(result);
        return Ok(new OASISResponse { Message = "Edge removed." });
    }

    [HttpGet("{questId:guid}/topological-order")]
    public async Task<ActionResult<OASISResult<IEnumerable<Guid>>>> GetTopologicalOrder(Guid questId, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<IEnumerable<Guid>> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.GetTopologicalOrderAsync(questId, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Quest Dependencies sub-resource ───

    [HttpPost("{questId:guid}/dependencies")]
    public async Task<ActionResult<OASISResult<QuestDependency>>> AddDependency(Guid questId, [FromBody] QuestDependencyCreateModel model, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<QuestDependency> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.AddDependencyAsync(questId, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{questId:guid}/dependencies/{depId:guid}")]
    public async Task<ActionResult<OASISResponse>> RemoveDependency(Guid questId, Guid depId, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<bool> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.RemoveDependencyAsync(questId, depId, avatarId.Value, request);
        if (result.IsError || !result.Result) return BadRequest(result);
        return Ok(new OASISResponse { Message = "Dependency removed." });
    }

    [HttpGet("{questId:guid}/dependency-status")]
    public async Task<ActionResult<OASISResult<DependencyCheckResult>>> GetDependencyStatus(Guid questId, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<DependencyCheckResult> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.CheckDependenciesAsync(questId, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── QuestRun read surface ───

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<OASISResult<QuestRun>>> GetRun(Guid runId, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<QuestRun> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.GetRunAsync(runId, avatarId.Value, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("{questId:guid}/runs")]
    public async Task<ActionResult<OASISResult<IEnumerable<QuestRun>>>> ListRunsByQuest(Guid questId, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<IEnumerable<QuestRun>> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.ListRunsByQuestAsync(questId, avatarId.Value, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("runs/{runId:guid}/execution-state")]
    public async Task<ActionResult<OASISResult<QuestExecutionState>>> GetExecutionState(Guid runId, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<QuestExecutionState> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.GetExecutionStateAsync(runId, avatarId.Value, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpPost("runs/{runId:guid}/complete")]
    public async Task<ActionResult<OASISResult<QuestRun>>> MarkRunCompleted(Guid runId, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<QuestRun> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.MarkRunCompletedAsync(runId, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
