using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.NodeAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Controllers;

/// <summary>Read and verify the hash-chained node-admin audit (H4/H5).</summary>
[ApiController]
[Route("api/node-admin/audit")]
[Authorize(Policy = NodeAdminPolicies.AuditRead)]
public sealed class NodeAdminAuditController(NodeAdminManager manager) : NodeAdminControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AZOAResult<NodeAdminAuditPageView>>> List([FromQuery] long afterSeq = 0, [FromQuery] int limit = 100, CancellationToken ct = default)
        => Respond(await manager.ListAuditAsync(afterSeq, limit, ct));

    [HttpGet("verify")]
    public async Task<ActionResult<AZOAResult<NodeAdminAuditVerifyView>>> Verify([FromQuery] long? fromSeq = null, [FromQuery] bool full = false,
        CancellationToken ct = default)
        => Respond(await manager.VerifyAuditAsync(fromSeq, full, ct));
}
