using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.NodeAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Controllers;

/// <summary>Node-admin roles; built-ins are immutable and every write is escalation- and last-owner-checked.</summary>
[ApiController]
[Route("api/node-admin/roles")]
[Authorize(Policy = NodeAdminPolicies.Session)]
public sealed class NodeAdminRolesController(NodeAdminManager manager) : NodeAdminControllerBase
{
    [HttpGet]
    [Authorize(Policy = NodeAdminPolicies.RolesRead)]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<NodeAdminRoleView>>>> List(CancellationToken ct)
        => Respond(await manager.ListRolesAsync(ct));

    [HttpPost]
    [Authorize(Policy = NodeAdminPolicies.RolesManageRecent)]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<AZOAResult<NodeAdminRoleView>>> Create([FromBody] NodeAdminRoleRequest request, CancellationToken ct)
        => Respond(await manager.CreateRoleAsync(Caller, request, ct));

    [HttpPut("{id}")]
    [Authorize(Policy = NodeAdminPolicies.RolesManageRecent)]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<AZOAResult<NodeAdminRoleView>>> Update(string id, [FromBody] NodeAdminRoleRequest request, CancellationToken ct)
        => Respond(await manager.UpdateRoleAsync(Caller, id, request, ct));

    [HttpDelete("{id}")]
    [Authorize(Policy = NodeAdminPolicies.RolesManageRecent)]
    public async Task<ActionResult<AZOAResult<bool>>> Delete(string id, CancellationToken ct)
        => Respond(await manager.DeleteRoleAsync(Caller, id, ct));
}
