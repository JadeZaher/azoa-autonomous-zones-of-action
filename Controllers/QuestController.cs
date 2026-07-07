using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

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
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<Quest>>> Create([FromBody] QuestCreateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.CreateAsync(model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<Quest>>> Get(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.GetAsync(id, avatarId.Value, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    /// <summary>Marketplace browse: public + published quests any authenticated avatar may fork/start.</summary>
    [HttpGet("public")]
    public async Task<ActionResult<AZOAResult<IEnumerable<Quest>>>> ListPublic([FromQuery] AZOARequest? request)
    {
        var result = await _questManager.ListPublicAsync(request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("avatar/{avatarId:guid}")]
    public async Task<ActionResult<AZOAResult<IEnumerable<Quest>>>> GetByAvatar(Guid avatarId, [FromQuery] AZOARequest? request)
    {
        var callerId = GetAvatarIdFromClaims();
        if (callerId == null)
            return Unauthorized(new AZOAResult<IEnumerable<Quest>> { IsError = true, Message = "Invalid token." });
        if (avatarId != callerId.Value)
            return StatusCode(StatusCodes.Status403Forbidden);

        var result = await _questManager.GetByAvatarAsync(avatarId, request);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<Quest>>> Update(Guid id, [FromBody] QuestUpdateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.UpdateAsync(id, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResponse>> Delete(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.DeleteAsync(id, avatarId.Value, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new AZOAResponse { Message = "Quest deleted." });
    }

    // ─── DAG validation ───

    [HttpPost("{id:guid}/validate")]
    public async Task<ActionResult<AZOAResult<bool>>> Validate(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _questManager.ValidateDAGAsync(id, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Definition lifecycle (FR-2, quest-dag-semantic-hardening) ───

    /// <summary>
    /// Runs the full validation stack and flips the quest from Draft to Active.
    /// See Managers/AGENTS.md §publish-lifecycle.
    /// </summary>
    [HttpPost("{id:guid}/publish")]
    public async Task<ActionResult<AZOAResult<Quest>>> Publish(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.PublishAsync(id, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Flips an Active quest back to Draft so its definition can be mutated.
    /// Refused while any in-flight runs exist.
    /// </summary>
    [HttpPost("{id:guid}/unpublish")]
    public async Task<ActionResult<AZOAResult<Quest>>> Unpublish(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.UnpublishAsync(id, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Execution ───

    [HttpPost("{id:guid}/execute")]
    public async Task<ActionResult<AZOAResult<QuestRun>>> Execute(Guid id, [FromQuery] AZOARequest? request, [FromQuery] bool acknowledgeEconomicEffects = false)
    {
        // Returns the produced QuestRun (one execution attempt). Runtime state
        // — per-node State/Output/Error — lives on the per-(run, node)
        // QuestNodeExecution rows (queryable separately via the run id).
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestRun> { IsError = true, Message = "Invalid token." });

        // tenant-consent-delegation AC4: a tenant-driven child credential carries
        // the act_as_tenant claim; thread it onto the run so a Tier-2 economic node
        // stamps it on the BlockchainOperation and the signing seam's consent gate
        // fires. A plain user principal yields null → no behavioural change.
        // acknowledgeEconomicEffects: runner consent to the disclosed value-moving
        // manifest on a non-owner marketplace run (see /preview) — see Managers/AGENTS.md §economic-consent.
        var result = await _questManager.ExecuteAsync(id, avatarId.Value, request, User.GetActingTenantId(), acknowledgeEconomicEffects);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Pre-commit disclosure: the value-moving-node manifest a runner would trigger
    /// by starting this quest, so a marketplace runner sees "this quest moves assets"
    /// BEFORE committing. See Managers/AGENTS.md §economic-consent.
    /// </summary>
    [HttpGet("{id:guid}/preview")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<QuestEconomicManifest>>> PreviewRun(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestEconomicManifest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.PreviewRunAsync(id, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/nodes/{nodeId:guid}/execute")]
    public async Task<ActionResult<AZOAResult<QuestNodeExecution>>> ExecuteNode(Guid id, Guid nodeId, [FromQuery] AZOARequest? request)
    {
        // Single-node execution produces an ad-hoc one-node QuestRun and
        // returns the QuestNodeExecution row for the result.
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestNodeExecution> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.ExecuteNodeAsync(id, nodeId, avatarId.Value, request, User.GetActingTenantId());
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("runs/{runId:guid}/fork")]
    public async Task<ActionResult<AZOAResult<QuestRun>>> Fork(Guid runId, [FromBody] QuestForkRequest body, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestRun> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.ForkAsync(runId, body.AtNodeId, body.Reason, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("runs/{runId:guid}/mark-failed")]
    public async Task<ActionResult<AZOAResult<QuestRun>>> MarkRunFailed(Guid runId, [FromBody] QuestMarkFailedRequest body, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestRun> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.MarkRunFailedAsync(runId, body.Reason, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Durable workflow engine (durable-workflow-engine) ───

    /// <summary>
    /// Start a durable, suspendable workflow run for a quest. Unlike
    /// <c>/{id}/execute</c> (which runs the whole DAG synchronously), this
    /// returns immediately and the engine advances the run asynchronously,
    /// suspending at manual/gate/timer nodes.
    /// </summary>
    [HttpPost("{id:guid}/start-workflow")]
    public async Task<ActionResult<AZOAResult<QuestRun>>> StartWorkflow(Guid id, [FromQuery] AZOARequest? request, [FromQuery] bool acknowledgeEconomicEffects = false)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestRun> { IsError = true, Message = "Invalid token." });

        // tenant-consent-delegation AC4: the durable path — persist the acting
        // tenant from the principal onto the run so it survives the async saga hop
        // to the Tier-2 node handlers. Null for a plain user → no behaviour change.
        // acknowledgeEconomicEffects: runner consent to the disclosed manifest on a
        // non-owner marketplace run (see /preview) — see Managers/AGENTS.md §economic-consent.
        var result = await _questManager.StartWorkflowRunAsync(id, avatarId.Value, request, User.GetActingTenantId(), acknowledgeEconomicEffects);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// The <c>step(nodeId)</c> primitive: resume a SUSPENDED manual-advance run
    /// from <c>fromNodeId</c> into its successor.
    /// </summary>
    [HttpPost("runs/{runId:guid}/advance")]
    public async Task<ActionResult<AZOAResult<QuestRun>>> Advance(Guid runId, [FromBody] QuestAdvanceRequest body, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestRun> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.AdvanceAsync(runId, body.FromNodeId, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Deliver an external signal to a PARKED gate node, un-parking it so the
    /// engine resumes the DAG.
    /// </summary>
    [HttpPost("runs/{runId:guid}/signal")]
    public async Task<ActionResult<AZOAResult<QuestRun>>> Signal(Guid runId, [FromBody] QuestSignalRequest body, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestRun> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.SignalAsync(runId, body.GateId, body.Payload, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Re-probe a run parked in <c>AwaitingReconciliation</c> (reconcile-before-retry,
    /// contract §7 / P7). Re-checks chain truth for the parked chain-action node(s):
    /// a Confirmed tx reconciles to success and resumes the DAG (NO re-mint), a
    /// FailedOnChain node is released to retry/compensation, and an indeterminate
    /// node stays parked. This NEVER re-broadcasts — it is the manual resolution
    /// hook for a "pending settlement" board state. Avatar-scoped.
    /// </summary>
    [HttpPost("runs/{runId:guid}/reconcile")]
    public async Task<ActionResult<AZOAResult<QuestReconciliationResult>>> Reconcile(Guid runId, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestReconciliationResult> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.ReconcileRunAsync(runId, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Operator sweep: re-probe EVERY run parked in <c>AwaitingReconciliation</c>
    /// (reconcile-before-retry, contract §7 / P7). The background/operator entry
    /// point that drains the pending-settlement backlog; same chain-truth logic as
    /// the per-run reconcile, applied to all parked runs. NEVER re-broadcasts.
    /// OPERATOR-ONLY: SweepReconciliationAsync is cross-avatar (unscoped), so this
    /// action requires the "Operator" admin policy — unlike the per-run reconcile
    /// above, which is avatar-scoped and open to any authenticated caller.
    /// </summary>
    [HttpPost("runs/reconcile-sweep")]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<AZOAResult<IEnumerable<QuestReconciliationResult>>>> ReconcileSweep([FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IEnumerable<QuestReconciliationResult>> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.SweepReconciliationAsync(request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Templates ───

    [HttpPost("templates")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<QuestTemplate>>> CreateTemplate([FromBody] QuestTemplateCreateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestTemplate> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.CreateTemplateAsync(model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("templates/{id:guid}")]
    public async Task<ActionResult<AZOAResult<QuestTemplate>>> GetTemplate(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _questManager.GetTemplateAsync(id, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("templates")]
    public async Task<ActionResult<AZOAResult<IEnumerable<QuestTemplate>>>> ListTemplates([FromQuery] AZOARequest? request)
    {
        var result = await _questManager.ListTemplatesAsync(request);
        return Ok(result);
    }

    [HttpPost("templates/{id:guid}/instantiate")]
    public async Task<ActionResult<AZOAResult<Quest>>> InstantiateTemplate(Guid id, [FromBody] Dictionary<string, string>? parameters, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.InstantiateTemplateAsync(id, avatarId.Value, parameters, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Node Templates ───

    [HttpPost("node-templates")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<QuestNodeTemplate>>> CreateNodeTemplate([FromBody] QuestNodeTemplateCreateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestNodeTemplate> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.CreateNodeTemplateAsync(model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("node-templates")]
    public async Task<ActionResult<AZOAResult<IEnumerable<QuestNodeTemplate>>>> ListNodeTemplates([FromQuery] AZOARequest? request)
    {
        var result = await _questManager.ListNodeTemplatesAsync(request);
        return Ok(result);
    }

    // ─── Quest Nodes sub-resource ───

    [HttpGet("{questId:guid}/nodes")]
    public async Task<ActionResult<AZOAResult<IEnumerable<QuestNode>>>> ListNodes(Guid questId, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IEnumerable<QuestNode>> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.ListNodesAsync(questId, avatarId.Value, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpPost("{questId:guid}/nodes")]
    public async Task<ActionResult<AZOAResult<QuestNode>>> AddNode(Guid questId, [FromBody] QuestNodeCreateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestNode> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.AddNodeAsync(questId, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{questId:guid}/nodes/{nodeId:guid}")]
    public async Task<ActionResult<AZOAResult<QuestNode>>> UpdateNode(Guid questId, Guid nodeId, [FromBody] QuestNodeUpdateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestNode> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.UpdateNodeAsync(questId, nodeId, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{questId:guid}/nodes/{nodeId:guid}")]
    public async Task<ActionResult<AZOAResponse>> DeleteNode(Guid questId, Guid nodeId, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<bool> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.DeleteNodeAsync(questId, nodeId, avatarId.Value, request);
        if (result.IsError || !result.Result) return BadRequest(result);
        return Ok(new AZOAResponse { Message = "Node deleted." });
    }

    // ─── Quest Edges sub-resource ───

    [HttpPost("{questId:guid}/edges")]
    public async Task<ActionResult<AZOAResult<QuestEdge>>> AddEdge(Guid questId, [FromBody] QuestEdgeAddModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestEdge> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.AddEdgeAsync(questId, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{questId:guid}/edges/{edgeId:guid}")]
    public async Task<ActionResult<AZOAResponse>> RemoveEdge(Guid questId, Guid edgeId, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<bool> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.RemoveEdgeAsync(questId, edgeId, avatarId.Value, request);
        if (result.IsError || !result.Result) return BadRequest(result);
        return Ok(new AZOAResponse { Message = "Edge removed." });
    }

    [HttpGet("{questId:guid}/topological-order")]
    public async Task<ActionResult<AZOAResult<IEnumerable<Guid>>>> GetTopologicalOrder(Guid questId, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IEnumerable<Guid>> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.GetTopologicalOrderAsync(questId, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Quest Dependencies sub-resource ───

    [HttpPost("{questId:guid}/dependencies")]
    public async Task<ActionResult<AZOAResult<QuestDependency>>> AddDependency(Guid questId, [FromBody] QuestDependencyCreateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestDependency> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.AddDependencyAsync(questId, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{questId:guid}/dependencies/{depId:guid}")]
    public async Task<ActionResult<AZOAResponse>> RemoveDependency(Guid questId, Guid depId, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<bool> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.RemoveDependencyAsync(questId, depId, avatarId.Value, request);
        if (result.IsError || !result.Result) return BadRequest(result);
        return Ok(new AZOAResponse { Message = "Dependency removed." });
    }

    [HttpGet("{questId:guid}/dependency-status")]
    public async Task<ActionResult<AZOAResult<DependencyCheckResult>>> GetDependencyStatus(Guid questId, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<DependencyCheckResult> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.CheckDependenciesAsync(questId, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── QuestRun read surface ───

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<AZOAResult<QuestRun>>> GetRun(Guid runId, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestRun> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.GetRunAsync(runId, avatarId.Value, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("{questId:guid}/runs")]
    public async Task<ActionResult<AZOAResult<IEnumerable<QuestRun>>>> ListRunsByQuest(Guid questId, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IEnumerable<QuestRun>> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.ListRunsByQuestAsync(questId, avatarId.Value, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("runs/{runId:guid}/execution-state")]
    public async Task<ActionResult<AZOAResult<QuestExecutionState>>> GetExecutionState(Guid runId, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestExecutionState> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.GetExecutionStateAsync(runId, avatarId.Value, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpPost("runs/{runId:guid}/complete")]
    public async Task<ActionResult<AZOAResult<QuestRun>>> MarkRunCompleted(Guid runId, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestRun> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.MarkRunCompletedAsync(runId, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Invitations + access requests (quest-invitations-approval) ───
    // Run-authorization is orthogonal to IsPublic (discoverability). Owner routes
    // reject non-owners; requester routes reject other requesters; body-supplied
    // avatar is ignored. See Managers/AGENTS.md §quest-invitations.

    /// <summary>Owner sets the run-access mode (Open ↔ InviteOnly) + optionally seeds the invite list.</summary>
    [HttpPut("{id:guid}/run-access")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<Quest>>> SetRunAccess(Guid id, [FromBody] QuestRunAccessRequest body, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.SetRunAccessAsync(id, avatarId.Value, body.RunAccess, body.InvitedAvatarIds, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Owner directly invites an avatar (no request needed; idempotent).</summary>
    [HttpPost("{id:guid}/invite")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<Quest>>> Invite(Guid id, [FromBody] QuestInviteRequest body, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.InviteAvatarAsync(id, avatarId.Value, body.AvatarId, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Owner revokes an invite (no-op when absent; in-flight runs unaffected).</summary>
    [HttpDelete("{id:guid}/invite/{avatarId:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<Quest>>> RevokeInvite(Guid id, Guid avatarId, [FromQuery] AZOARequest? request)
    {
        var callerId = GetAvatarIdFromClaims();
        if (callerId == null)
            return Unauthorized(new AZOAResult<Quest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.RevokeInviteAsync(id, callerId.Value, avatarId, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Any viewer opens a Pending access request for an InviteOnly quest (idempotent).</summary>
    [HttpPost("{id:guid}/access-requests")]
    public async Task<ActionResult<AZOAResult<QuestAccessRequest>>> RequestAccess(Guid id, [FromBody] QuestAccessOpenRequest body, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestAccessRequest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.RequestAccessAsync(id, avatarId.Value, body?.Message, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Owner approval queue: the quest's access requests, optionally status-filtered.</summary>
    [HttpGet("{id:guid}/access-requests")]
    public async Task<ActionResult<AZOAResult<IEnumerable<QuestAccessRequest>>>> ListAccessRequests(Guid id, [FromQuery] QuestAccessRequestStatus? status, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IEnumerable<QuestAccessRequest>> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.ListAccessRequestsAsync(id, avatarId.Value, status, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Owner approves (mints invite) or rejects a Pending access request.</summary>
    [HttpPost("access-requests/{requestId:guid}/decision")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<QuestAccessRequest>>> DecideAccessRequest(Guid requestId, [FromBody] QuestAccessDecisionRequest body, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestAccessRequest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.DecideAccessRequestAsync(requestId, avatarId.Value, body.Approve, body.Reason, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Requester withdraws their own Pending access request.</summary>
    [HttpPost("access-requests/{requestId:guid}/withdraw")]
    public async Task<ActionResult<AZOAResult<QuestAccessRequest>>> WithdrawAccessRequest(Guid requestId, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<QuestAccessRequest> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.WithdrawAccessRequestAsync(requestId, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Requester's own outbound access requests, optionally status-filtered.</summary>
    [HttpGet("access-requests/mine")]
    public async Task<ActionResult<AZOAResult<IEnumerable<QuestAccessRequest>>>> ListMyAccessRequests([FromQuery] QuestAccessRequestStatus? status, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IEnumerable<QuestAccessRequest>> { IsError = true, Message = "Invalid token." });

        var result = await _questManager.ListMyAccessRequestsAsync(avatarId.Value, status, request);
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
