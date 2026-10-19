using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.NodeAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Controllers;

/// <summary>Named node-admin lifecycle, keys and MFA (plan §3 + §8 C1/H3); writes require step-up.</summary>
[ApiController]
[Route("api/node-admin/admins")]
[Authorize(Policy = NodeAdminPolicies.Session)]
public sealed class NodeAdminAdminsController(NodeAdminManager manager) : NodeAdminControllerBase
{
    [HttpGet]
    [Authorize(Policy = NodeAdminPolicies.AdminsRead)]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<NodeAdminView>>>> List(CancellationToken ct)
        => Respond(await manager.ListAdminsAsync(ct));

    [HttpPost]
    [Authorize(Policy = NodeAdminPolicies.AdminsManageRecent)]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<AZOAResult<NodeAdminCreatedView>>> Create([FromBody] NodeAdminCreateRequest request, CancellationToken ct)
        => Respond(await manager.CreateAdminAsync(Caller, request, ct));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = NodeAdminPolicies.AdminsRead)]
    public async Task<ActionResult<AZOAResult<NodeAdminView>>> Get(Guid id, CancellationToken ct)
        => Respond(await manager.GetAdminAsync(id, ct));

    [HttpPost("{id:guid}/suspend")]
    [Authorize(Policy = NodeAdminPolicies.AdminsManageRecent)]
    public async Task<ActionResult<AZOAResult<NodeAdminView>>> Suspend(Guid id, CancellationToken ct)
        => Respond(await manager.SuspendAsync(Caller, id, ct));

    [HttpPost("{id:guid}/reactivate")]
    [Authorize(Policy = NodeAdminPolicies.AdminsManageRecent)]
    public async Task<ActionResult<AZOAResult<NodeAdminView>>> Reactivate(Guid id, CancellationToken ct)
        => Respond(await manager.ReactivateAsync(Caller, id, ct));

    [HttpPost("{id:guid}/revoke")]
    [Authorize(Policy = NodeAdminPolicies.AdminsManageRecent)]
    public async Task<ActionResult<AZOAResult<NodeAdminView>>> Revoke(Guid id, CancellationToken ct)
        => Respond(await manager.RevokeAsync(Caller, id, ct));

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = NodeAdminPolicies.AdminsManageRecent)]
    [RequestSizeLimit(4096)]
    public async Task<ActionResult<AZOAResult<NodeAdminView>>> Activate(Guid id, [FromBody] NodeAdminActivateRequest request, CancellationToken ct)
        => Respond(await manager.ActivateAsync(Caller, id, request, ct));

    [HttpPut("{id:guid}/roles")]
    [Authorize(Policy = NodeAdminPolicies.RolesManageRecent)]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<AZOAResult<NodeAdminReenrollmentView>>> AssignRoles(Guid id, [FromBody] NodeAdminAssignRolesRequest request, CancellationToken ct)
        => Respond(await manager.AssignRolesAsync(Caller, id, request, ct));

    [HttpPost("{id:guid}/revoke-sessions")]
    [Authorize(Policy = NodeAdminPolicies.AdminsManageRecent)]
    public async Task<ActionResult<AZOAResult<bool>>> RevokeSessions(Guid id, CancellationToken ct)
        => Respond(await manager.RevokeAdminSessionsAsync(Caller, id, ct));

    [HttpPost("{id:guid}/keys")]
    [Authorize(Policy = NodeAdminPolicies.SessionRecent)]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<AZOAResult<NodeAdminKeyView>>> AddKey(Guid id, [FromBody] NodeAdminAddKeyRequest request, CancellationToken ct)
        => Respond(await manager.AddKeyAsync(Caller, id, request, ct));

    [HttpDelete("{id:guid}/keys/{fingerprint}")]
    [Authorize(Policy = NodeAdminPolicies.SessionRecent)]
    public async Task<ActionResult<AZOAResult<NodeAdminKeyView>>> RevokeKey(Guid id, string fingerprint, CancellationToken ct)
        => Respond(await manager.RevokeKeyAsync(Caller, id, fingerprint, ct));

    [HttpPost("{id:guid}/mfa/reset")]
    [Authorize(Policy = NodeAdminPolicies.AdminsManageRecent)]
    public async Task<ActionResult<AZOAResult<NodeAdminReenrollmentView>>> ResetMfa(Guid id, CancellationToken ct)
        => Respond(await manager.ResetMfaAsync(Caller, id, ct));
}
