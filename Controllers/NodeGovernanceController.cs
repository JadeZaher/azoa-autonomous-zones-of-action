using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.NodeAdmin;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/node-governance")]
[Authorize(Policy = "NodeGovern")]
public sealed class NodeGovernanceController : ControllerBase
{
    private readonly INodeGovernanceManager _manager;
    private readonly INodeFeeScheduleManager _feeManager;
    private readonly INodeTreasuryManager _treasuryManager;

    public NodeGovernanceController(
        INodeGovernanceManager manager,
        INodeFeeScheduleManager feeManager,
        INodeTreasuryManager treasuryManager)
    {
        _manager = manager;
        _feeManager = feeManager;
        _treasuryManager = treasuryManager;
    }

    [HttpGet("parameters")]
    public async Task<ActionResult<AZOAResult<NodeGovernanceParametersResponse>>> GetParameters(CancellationToken ct)
        => Ok(await _manager.GetParametersAsync(ct));

    [HttpPut("parameters")]
    public async Task<ActionResult<AZOAResult<NodeGovernanceParametersResponse>>> UpdateParameters(
        [FromBody] NodeGovernanceParametersUpdateRequest request,
        CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor))
            return Unauthorized();

        var result = await _manager.UpdateParametersAsync(request, operatorActor.SubjectUserId, operatorActor, ct);
        if (result.IsError && result.Message.Contains("version conflict", StringComparison.OrdinalIgnoreCase))
            return Conflict(result);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("audit")]
    public async Task<ActionResult<AZOAResult<IEnumerable<NodeGovernanceAuditResponse>>>> ListAudit(
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
        => Ok(await _manager.ListAuditAsync(limit, ct));

    [HttpGet("fee-schedule")]
    public async Task<ActionResult<AZOAResult<NodeFeeScheduleResponse>>> GetFeeSchedule(CancellationToken ct)
        => Ok(await _feeManager.GetScheduleAsync(ct));

    [HttpPut("fee-schedule")]
    public async Task<ActionResult<AZOAResult<NodeFeeScheduleResponse>>> UpdateFeeSchedule(
        [FromBody] NodeFeeScheduleUpdateRequest request,
        CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor))
            return Unauthorized();

        var result = await _feeManager.UpdateScheduleAsync(request, operatorActor.SubjectUserId, operatorActor, ct);
        if (result.IsError && result.Message.Contains("version conflict", StringComparison.OrdinalIgnoreCase))
            return Conflict(result);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("fee-audit")]
    public async Task<ActionResult<AZOAResult<IEnumerable<NodeFeeAuditResponse>>>> ListFeeAudit(
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
        => Ok(await _feeManager.ListAuditAsync(limit, ct));

    [HttpGet("treasury/{chain}/{network}")]
    public async Task<ActionResult<AZOAResult<NodeTreasuryDestinationResponse>>> GetTreasuryDestination(
        string chain,
        ChainNetwork network,
        CancellationToken ct)
        => Ok(await _treasuryManager.GetDestinationAsync(chain, network, ct));

    [HttpPut("treasury")]
    public async Task<ActionResult<AZOAResult<NodeTreasuryDestinationResponse>>> UpdateTreasuryDestination(
        [FromBody] NodeTreasuryDestinationUpdateRequest request,
        CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor))
            return Unauthorized();

        var result = await _treasuryManager.UpdateDestinationAsync(request, operatorActor.SubjectUserId, operatorActor, ct);
        if (result.IsError && result.Message.Contains("version conflict", StringComparison.OrdinalIgnoreCase))
            return Conflict(result);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("treasury-audit")]
    public async Task<ActionResult<AZOAResult<IEnumerable<NodeTreasuryAuditResponse>>>> ListTreasuryAudit(
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
        => Ok(await _treasuryManager.ListAuditAsync(limit, ct));
}
