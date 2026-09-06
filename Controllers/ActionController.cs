using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ActionController : ControllerBase
{
    private readonly IActionManager _actionManager;

    public ActionController(IActionManager actionManager)
    {
        _actionManager = actionManager;
    }

    // ─── ActionDefinition CRUD ───

    [HttpPost]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<ActionDefinition>>> Create([FromBody] ActionCreateModel model, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionDefinition> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.CreateAsync(model, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<ActionDefinition>>> Get(Guid id, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionDefinition> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.GetAsync(id, userId.Value, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    /// <summary>Marketplace browse: public + published actions any authenticated user may fork/start.</summary>
    [HttpGet("public")]
    public async Task<ActionResult<AZOAResult<IEnumerable<ActionDefinition>>>> ListPublic([FromQuery] AZOARequest? request)
    {
        var result = await _actionManager.ListPublicAsync(request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("user/{userId:guid}")]
    public async Task<ActionResult<AZOAResult<IEnumerable<ActionDefinition>>>> GetByUser(Guid userId, [FromQuery] AZOARequest? request)
    {
        var callerId = GetUserIdFromClaims();
        if (callerId == null)
            return Unauthorized(new AZOAResult<IEnumerable<ActionDefinition>> { IsError = true, Message = "Invalid token." });
        if (userId != callerId.Value)
            return StatusCode(StatusCodes.Status403Forbidden);

        var result = await _actionManager.GetByUserAsync(userId, request);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<ActionDefinition>>> Update(Guid id, [FromBody] ActionUpdateModel model, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionDefinition> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.UpdateAsync(id, model, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResponse>> Delete(Guid id, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionDefinition> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.DeleteAsync(id, userId.Value, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new AZOAResponse { Message = "ActionDefinition deleted." });
    }

    // ─── DAG validation ───

    [HttpPost("{id:guid}/validate")]
    public async Task<ActionResult<AZOAResult<bool>>> Validate(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _actionManager.ValidateDAGAsync(id, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Definition lifecycle (FR-2, action-dag-semantic-hardening) ───

    /// <summary>
    /// Runs the full validation stack and flips the action from Draft to Active.
    /// See Managers/AGENTS.md §publish-lifecycle.
    /// </summary>
    [HttpPost("{id:guid}/publish")]
    public async Task<ActionResult<AZOAResult<ActionDefinition>>> Publish(Guid id, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionDefinition> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.PublishAsync(id, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Flips an Active action back to Draft so its definition can be mutated.
    /// Refused while any in-flight runs exist.
    /// </summary>
    [HttpPost("{id:guid}/unpublish")]
    public async Task<ActionResult<AZOAResult<ActionDefinition>>> Unpublish(Guid id, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionDefinition> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.UnpublishAsync(id, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Execution ───

    [HttpPost("{id:guid}/execute")]
    public async Task<ActionResult<AZOAResult<ActionRun>>> Execute(Guid id, [FromQuery] AZOARequest? request, [FromQuery] bool acknowledgeEconomicEffects = false)
    {
        // Returns the produced ActionRun (one execution attempt). Runtime state
        // — per-node State/Output/Error — lives on the per-(run, node)
        // ActionNodeExecution rows (queryable separately via the run id).
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionRun> { IsError = true, Message = "Invalid token." });

        // tenant-consent-delegation AC4: a tenant-driven child credential carries
        // the act_as_tenant claim; thread it onto the run so a Tier-2 economic node
        // stamps it on the BlockchainOperation and the signing seam's consent gate
        // fires. A plain user principal yields null → no behavioural change.
        // acknowledgeEconomicEffects: runner consent to the disclosed value-moving
        // manifest on a non-owner marketplace run (see /preview) — see Managers/AGENTS.md §economic-consent.
        var result = await _actionManager.ExecuteAsync(id, userId.Value, request, User.GetActingTenantId(), acknowledgeEconomicEffects);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Pre-commit disclosure: the value-moving-node manifest a runner would trigger
    /// by starting this action, so a marketplace runner sees "this action moves assets"
    /// BEFORE committing. See Managers/AGENTS.md §economic-consent.
    /// </summary>
    [HttpGet("{id:guid}/preview")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<ActionEconomicManifest>>> PreviewRun(Guid id, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionEconomicManifest> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.PreviewRunAsync(id, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/nodes/{nodeId:guid}/execute")]
    public async Task<ActionResult<AZOAResult<ActionNodeExecutionResponse>>> ExecuteNode(Guid id, Guid nodeId, [FromQuery] AZOARequest? request)
    {
        // Single-node execution produces an ad-hoc one-node ActionRun and returns
        // a public projection of its node execution. The durable row retains the
        // workflow-binding output; its HTTP projection is strictly allowlisted.
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionNodeExecutionResponse> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.ExecuteNodeAsync(id, nodeId, userId.Value, request, User.GetActingTenantId());
        var response = Project(result, ActionNodeOutputProjection.ToPublic);
        if (response.IsError) return BadRequest(response);
        return Ok(response);
    }

    [HttpPost("runs/{runId:guid}/fork")]
    public async Task<ActionResult<AZOAResult<ActionRun>>> Fork(Guid runId, [FromBody] ActionForkRequest body, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionRun> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.ForkAsync(runId, body.AtNodeId, body.Reason, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("runs/{runId:guid}/mark-failed")]
    public async Task<ActionResult<AZOAResult<ActionRun>>> MarkRunFailed(Guid runId, [FromBody] ActionMarkFailedRequest body, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionRun> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.MarkRunFailedAsync(runId, body.Reason, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Durable workflow engine (durable-workflow-engine) ───

    /// <summary>
    /// Start a durable, suspendable workflow run for an action. Unlike
    /// <c>/{id}/execute</c> (which runs the whole DAG synchronously), this
    /// returns immediately and the engine advances the run asynchronously,
    /// suspending at manual/gate/timer nodes.
    /// </summary>
    [HttpPost("{id:guid}/start-workflow")]
    public async Task<ActionResult<AZOAResult<ActionRun>>> StartWorkflow(Guid id, [FromQuery] AZOARequest? request, [FromQuery] bool acknowledgeEconomicEffects = false)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionRun> { IsError = true, Message = "Invalid token." });

        // tenant-consent-delegation AC4: the durable path — persist the acting
        // tenant from the principal onto the run so it survives the async saga hop
        // to the Tier-2 node handlers. Null for a plain user → no behaviour change.
        // acknowledgeEconomicEffects: runner consent to the disclosed manifest on a
        // non-owner marketplace run (see /preview) — see Managers/AGENTS.md §economic-consent.
        var result = await _actionManager.StartWorkflowRunAsync(id, userId.Value, request, User.GetActingTenantId(), acknowledgeEconomicEffects);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// The <c>step(nodeId)</c> primitive: resume a SUSPENDED manual-advance run
    /// from <c>fromNodeId</c> into its successor.
    /// </summary>
    [HttpPost("runs/{runId:guid}/advance")]
    public async Task<ActionResult<AZOAResult<ActionRun>>> Advance(Guid runId, [FromBody] ActionAdvanceRequest body, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionRun> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.AdvanceAsync(runId, body.FromNodeId, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Deliver an external signal to a PARKED gate node, un-parking it so the
    /// engine resumes the DAG.
    /// </summary>
    [HttpPost("runs/{runId:guid}/signal")]
    public async Task<ActionResult<AZOAResult<ActionRun>>> Signal(Guid runId, [FromBody] ActionSignalRequest body, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionRun> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.SignalAsync(runId, body.GateId, body.Payload, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Re-probe a run parked in <c>AwaitingReconciliation</c> (reconcile-before-retry,
    /// contract §7 / P7). Re-checks chain truth for the parked chain-action node(s):
    /// a Confirmed tx reconciles to success and resumes the DAG (NO re-mint), a
    /// FailedOnChain node is released to retry/compensation, and an indeterminate
    /// node stays parked. This NEVER re-broadcasts — it is the manual resolution
    /// hook for a "pending settlement" board state. User-scoped.
    /// </summary>
    [HttpPost("runs/{runId:guid}/reconcile")]
    public async Task<ActionResult<AZOAResult<ActionReconciliationResult>>> Reconcile(Guid runId, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionReconciliationResult> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.ReconcileRunAsync(runId, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Operator sweep: re-probe EVERY run parked in <c>AwaitingReconciliation</c>
    /// (reconcile-before-retry, contract §7 / P7). The background/operator entry
    /// point that drains the pending-settlement backlog; same chain-truth logic as
    /// the per-run reconcile, applied to all parked runs. NEVER re-broadcasts.
    /// OPERATOR-ONLY: SweepReconciliationAsync is cross-user (unscoped), so this
    /// action requires the "Operator" admin policy — unlike the per-run reconcile
    /// above, which is user-scoped and open to any authenticated caller.
    /// </summary>
    [HttpPost("runs/reconcile-sweep")]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<AZOAResult<IEnumerable<ActionReconciliationResult>>>> ReconcileSweep([FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IEnumerable<ActionReconciliationResult>> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.SweepReconciliationAsync(request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Templates ───

    [HttpPost("templates")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<ActionTemplate>>> CreateTemplate([FromBody] ActionTemplateCreateModel model, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionTemplate> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.CreateTemplateAsync(model, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("templates/{id:guid}")]
    public async Task<ActionResult<AZOAResult<ActionTemplate>>> GetTemplate(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _actionManager.GetTemplateAsync(id, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("templates")]
    public async Task<ActionResult<AZOAResult<IEnumerable<ActionTemplate>>>> ListTemplates([FromQuery] AZOARequest? request)
    {
        var result = await _actionManager.ListTemplatesAsync(request);
        return Ok(result);
    }

    [HttpPost("templates/{id:guid}/instantiate")]
    public async Task<ActionResult<AZOAResult<ActionDefinition>>> InstantiateTemplate(Guid id, [FromBody] Dictionary<string, string>? parameters, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionDefinition> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.InstantiateTemplateAsync(id, userId.Value, parameters, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Node Templates ───

    [HttpPost("node-templates")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<ActionNodeTemplate>>> CreateNodeTemplate([FromBody] ActionNodeTemplateCreateModel model, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionNodeTemplate> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.CreateNodeTemplateAsync(model, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("node-templates")]
    public async Task<ActionResult<AZOAResult<IEnumerable<ActionNodeTemplate>>>> ListNodeTemplates([FromQuery] AZOARequest? request)
    {
        var result = await _actionManager.ListNodeTemplatesAsync(request);
        return Ok(result);
    }

    // ─── ActionDefinition Nodes sub-resource ───

    [HttpGet("{actionId:guid}/nodes")]
    public async Task<ActionResult<AZOAResult<IEnumerable<ActionNode>>>> ListNodes(Guid actionId, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IEnumerable<ActionNode>> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.ListNodesAsync(actionId, userId.Value, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpPost("{actionId:guid}/nodes")]
    public async Task<ActionResult<AZOAResult<ActionNode>>> AddNode(Guid actionId, [FromBody] ActionNodeCreateModel model, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionNode> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.AddNodeAsync(actionId, model, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{actionId:guid}/nodes/{nodeId:guid}")]
    public async Task<ActionResult<AZOAResult<ActionNode>>> UpdateNode(Guid actionId, Guid nodeId, [FromBody] ActionNodeUpdateModel model, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionNode> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.UpdateNodeAsync(actionId, nodeId, model, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{actionId:guid}/nodes/{nodeId:guid}")]
    public async Task<ActionResult<AZOAResponse>> DeleteNode(Guid actionId, Guid nodeId, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<bool> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.DeleteNodeAsync(actionId, nodeId, userId.Value, request);
        if (result.IsError || !result.Result) return BadRequest(result);
        return Ok(new AZOAResponse { Message = "Node deleted." });
    }

    // ─── ActionDefinition Edges sub-resource ───

    [HttpPost("{actionId:guid}/edges")]
    public async Task<ActionResult<AZOAResult<ActionEdge>>> AddEdge(Guid actionId, [FromBody] ActionEdgeAddModel model, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionEdge> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.AddEdgeAsync(actionId, model, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{actionId:guid}/edges/{edgeId:guid}")]
    public async Task<ActionResult<AZOAResponse>> RemoveEdge(Guid actionId, Guid edgeId, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<bool> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.RemoveEdgeAsync(actionId, edgeId, userId.Value, request);
        if (result.IsError || !result.Result) return BadRequest(result);
        return Ok(new AZOAResponse { Message = "Edge removed." });
    }

    [HttpGet("{actionId:guid}/topological-order")]
    public async Task<ActionResult<AZOAResult<IEnumerable<Guid>>>> GetTopologicalOrder(Guid actionId, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IEnumerable<Guid>> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.GetTopologicalOrderAsync(actionId, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── ActionDefinition Dependencies sub-resource ───

    [HttpPost("{actionId:guid}/dependencies")]
    public async Task<ActionResult<AZOAResult<ActionDependency>>> AddDependency(Guid actionId, [FromBody] ActionDependencyCreateModel model, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionDependency> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.AddDependencyAsync(actionId, model, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{actionId:guid}/dependencies/{depId:guid}")]
    public async Task<ActionResult<AZOAResponse>> RemoveDependency(Guid actionId, Guid depId, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<bool> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.RemoveDependencyAsync(actionId, depId, userId.Value, request);
        if (result.IsError || !result.Result) return BadRequest(result);
        return Ok(new AZOAResponse { Message = "Dependency removed." });
    }

    [HttpGet("{actionId:guid}/dependency-status")]
    public async Task<ActionResult<AZOAResult<DependencyCheckResult>>> GetDependencyStatus(Guid actionId, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<DependencyCheckResult> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.CheckDependenciesAsync(actionId, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── ActionRun read surface ───

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<AZOAResult<ActionRun>>> GetRun(Guid runId, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionRun> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.GetRunAsync(runId, userId.Value, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("{actionId:guid}/runs")]
    public async Task<ActionResult<AZOAResult<IEnumerable<ActionRun>>>> ListRunsByAction(Guid actionId, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IEnumerable<ActionRun>> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.ListRunsByActionAsync(actionId, userId.Value, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("runs/{runId:guid}/execution-state")]
    public async Task<ActionResult<AZOAResult<ActionExecutionStateResponse>>> GetExecutionState(Guid runId, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionExecutionStateResponse> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.GetExecutionStateAsync(runId, userId.Value, request);
        var response = Project(result, ActionNodeOutputProjection.ToPublic);
        if (response.IsError || response.Result == null) return NotFound(response);
        return Ok(response);
    }

    [HttpPost("runs/{runId:guid}/complete")]
    public async Task<ActionResult<AZOAResult<ActionRun>>> MarkRunCompleted(Guid runId, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionRun> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.MarkRunCompletedAsync(runId, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Invitations + access requests (action-invitations-approval) ───
    // Run-authorization is orthogonal to IsPublic (discoverability). Owner routes
    // reject non-owners; requester routes reject other requesters; body-supplied
    // user is ignored. See Managers/AGENTS.md §action-invitations.

    /// <summary>Owner sets the run-access mode (Open ↔ InviteOnly) + optionally seeds the invite list.</summary>
    [HttpPut("{id:guid}/run-access")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<ActionDefinition>>> SetRunAccess(Guid id, [FromBody] ActionRunAccessRequest body, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionDefinition> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.SetRunAccessAsync(id, userId.Value, body.RunAccess, body.InvitedUserIds, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Owner directly invites a user (no request needed; idempotent).</summary>
    [HttpPost("{id:guid}/invite")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<ActionDefinition>>> Invite(Guid id, [FromBody] ActionInviteRequest body, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionDefinition> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.InviteUserAsync(id, userId.Value, body.UserId, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Owner revokes an invite (no-op when absent; in-flight runs unaffected).</summary>
    [HttpDelete("{id:guid}/invite/{userId:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<ActionDefinition>>> RevokeInvite(Guid id, Guid userId, [FromQuery] AZOARequest? request)
    {
        var callerId = GetUserIdFromClaims();
        if (callerId == null)
            return Unauthorized(new AZOAResult<ActionDefinition> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.RevokeInviteAsync(id, callerId.Value, userId, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Any viewer opens a Pending access request for an InviteOnly action (idempotent).</summary>
    [HttpPost("{id:guid}/access-requests")]
    public async Task<ActionResult<AZOAResult<ActionAccessRequest>>> RequestAccess(Guid id, [FromBody] ActionAccessOpenRequest body, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionAccessRequest> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.RequestAccessAsync(id, userId.Value, body?.Message, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Owner approval queue: the action's access requests, optionally status-filtered.</summary>
    [HttpGet("{id:guid}/access-requests")]
    public async Task<ActionResult<AZOAResult<IEnumerable<ActionAccessRequest>>>> ListAccessRequests(Guid id, [FromQuery] ActionAccessRequestStatus? status, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IEnumerable<ActionAccessRequest>> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.ListAccessRequestsAsync(id, userId.Value, status, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Owner approves (mints invite) or rejects a Pending access request.</summary>
    [HttpPost("access-requests/{requestId:guid}/decision")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<ActionAccessRequest>>> DecideAccessRequest(Guid requestId, [FromBody] ActionAccessDecisionRequest body, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionAccessRequest> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.DecideAccessRequestAsync(requestId, userId.Value, body.Approve, body.Reason, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Requester withdraws their own Pending access request.</summary>
    [HttpPost("access-requests/{requestId:guid}/withdraw")]
    public async Task<ActionResult<AZOAResult<ActionAccessRequest>>> WithdrawAccessRequest(Guid requestId, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<ActionAccessRequest> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.WithdrawAccessRequestAsync(requestId, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Requester's own outbound access requests, optionally status-filtered.</summary>
    [HttpGet("access-requests/mine")]
    public async Task<ActionResult<AZOAResult<IEnumerable<ActionAccessRequest>>>> ListMyAccessRequests([FromQuery] ActionAccessRequestStatus? status, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IEnumerable<ActionAccessRequest>> { IsError = true, Message = "Invalid token." });

        var result = await _actionManager.ListMyAccessRequestsAsync(userId.Value, status, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    private Guid? GetUserIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private static AZOAResult<TOutput> Project<TInput, TOutput>(
        AZOAResult<TInput> result,
        Func<TInput, TOutput> project)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(project);

        return new AZOAResult<TOutput>
        {
            IsError = result.IsError,
            Message = result.Message,
            Result = result.Result is null ? default : project(result.Result),
            Code = result.Code,
            RetryAfterSeconds = result.RetryAfterSeconds,
        };
    }
}
