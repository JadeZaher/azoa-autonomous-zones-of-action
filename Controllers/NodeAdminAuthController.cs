using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Services.NodeAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>Node-admin sign-in, sessions and enrollment (plan §3 + §8); see <c>Controllers/AGENTS.md</c> §node-admin.</summary>
[ApiController]
[Route("api/node-admin")]
[Authorize(Policy = NodeAdminPolicies.Session)]
public sealed class NodeAdminAuthController(NodeAdminAuthManager auth, NodeAdminManager manager) : NodeAdminControllerBase
{
    [HttpPost("auth/challenge")]
    [AllowAnonymous]
    [CredentialFreePublicEndpoint]
    [EnableRateLimiting("operator-login")]
    [RequestSizeLimit(4096)]
    public async Task<ActionResult<AZOAResult<NodeAdminChallengeResponse>>> Challenge([FromBody] NodeAdminChallengeRequest request, CancellationToken ct)
        => Respond(await auth.CreateChallengeAsync(request, ct));

    [HttpPost("auth/session")]
    [AllowAnonymous]
    [CredentialFreePublicEndpoint]
    [EnableRateLimiting("operator-login")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<AZOAResult<NodeAdminSessionResponse>>> Session([FromBody] NodeAdminSessionRequest request, CancellationToken ct)
        => Respond(await auth.CreateSessionAsync(request, NodeAdminRequestInfo.From(HttpContext), ct));

    [HttpPost("auth/revoke")]
    public async Task<ActionResult<AZOAResult<bool>>> Revoke(CancellationToken ct)
        => Respond(await auth.RevokeOwnAsync(Caller, ct));

    [HttpGet("sessions")]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<NodeAdminSessionView>>>> Sessions([FromQuery] string? principal, CancellationToken ct)
        => Respond(await manager.ListSessionsAsync(Caller, principal, ct));

    [HttpPost("sessions/{handle}/revoke")]
    public async Task<ActionResult<AZOAResult<bool>>> RevokeSession(string handle, CancellationToken ct)
        => Respond(await manager.RevokeSessionAsync(Caller, handle, ct));

    [HttpPost("root/revoke-sessions")]
    [Authorize(Policy = NodeAdminPolicies.AdminsManageRecent)]
    public async Task<ActionResult<AZOAResult<bool>>> RevokeRootSessions(CancellationToken ct)
        => Respond(await manager.RevokeRootSessionsAsync(Caller, ct));

    [HttpGet("me")]
    public async Task<ActionResult<AZOAResult<NodeAdminMeView>>> Me(CancellationToken ct)
        => Respond(await manager.MeAsync(Caller, ct));

    [HttpPost("enroll/start")]
    [AllowAnonymous]
    [CredentialFreePublicEndpoint]
    [EnableRateLimiting("operator-login")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<AZOAResult<NodeAdminEnrollStartView>>> EnrollStart([FromBody] NodeAdminEnrollStartRequest request, CancellationToken ct)
        => Respond(await auth.StartEnrollmentAsync(request, NodeAdminRequestInfo.From(HttpContext), ct));

    [HttpPost("enroll/complete")]
    [AllowAnonymous]
    [CredentialFreePublicEndpoint]
    [EnableRateLimiting("operator-login")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<AZOAResult<NodeAdminEnrollCompleteView>>> EnrollComplete([FromBody] NodeAdminEnrollCompleteRequest request, CancellationToken ct)
        => Respond(await auth.CompleteEnrollmentAsync(request, NodeAdminRequestInfo.From(HttpContext), ct));
}
