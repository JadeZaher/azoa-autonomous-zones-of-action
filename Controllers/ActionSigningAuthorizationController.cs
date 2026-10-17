using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Authorize]
[Route("api/action/executions/{executionId:guid}/signing-authorization")]
public sealed class ActionSigningAuthorizationController(ActionSigningAuthorizationService service) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Authorize(Guid executionId, ActionSigningAuthorizationRequest request, CancellationToken ct)
    {
        var result = await service.AuthorizeAsync(executionId, request, User, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }
}
