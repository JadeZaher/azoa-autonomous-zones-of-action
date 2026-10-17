using System.Security.Claims;
using System.Text;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/deployments")]
[Authorize]
public sealed class DeploymentsController(IDeploymentService deployments,
    SigningAdmissionAuthorityFactory? signingAuthority = null) : ControllerBase
{
    [HttpPost("bundles/{bundleId:guid}/generate")]
    [DelegatedChildCredential(AzoaScopes.DappManage)]
    [Authorize(Policy = "DappManage")]
    [RequestSizeLimit(3 * 1024 * 1024)]
    public async Task<IActionResult> Generate(Guid bundleId, DeploymentGenerateRequest request, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Respond(await deployments.GenerateAsync(bundleId, actor, User.GetActingTenantId(), request, ct));

    [HttpPost]
    [Authorize(Policy = "DappManage")]
    [DelegatedChildCredential(AzoaScopes.DappManage, AzoaScopes.ContractDeploySign, AzoaScopes.ContractCallSign, AzoaScopes.SolanaAccountSign)]
    [RequestSizeLimit(3 * 1024 * 1024)]
    public async Task<IActionResult> Start(DeploymentStartRequest request, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Receipt(await deployments.StartWithCredentialAsync(actor, User.GetActingTenantId(),
            Request.Headers["Idempotency-Key"].ToString(), request, await CredentialAsync(ct), ct));

    [HttpGet("artifacts/{artifactId}")]
    [DelegatedChildCredential(AzoaScopes.DappManage)]
    public async Task<IActionResult> Artifact(string artifactId, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Respond(await deployments.GetArtifactAsync(artifactId, actor, User.GetActingTenantId(), ct));

    [HttpPost("quote")]
    [DelegatedChildCredential(AzoaScopes.DappManage)]
    [Authorize(Policy = "DappManage")]
    [RequestSizeLimit(3 * 1024 * 1024)]
    public async Task<IActionResult> Quote(DeploymentQuoteRequest request, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Respond(await deployments.QuoteAsync(actor, User.GetActingTenantId(), request, ct));

    [HttpPost("contract-query")]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> QueryContract(ContractQueryRequest request, CancellationToken ct)
        => Respond(await deployments.QueryContractAsync(request, ct));

    [HttpGet]
    [DelegatedChildCredential(AzoaScopes.DappManage)]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int limit = 25, CancellationToken ct = default)
        => Actor() is not { } actor ? Unauthorized() : Respond(await deployments.ListAsync(actor, User.GetActingTenantId(), cursor, limit, ct));

    [HttpGet("{id}")]
    [DelegatedChildCredential(AzoaScopes.DappManage)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Respond(await deployments.GetAsync(id, actor, User.GetActingTenantId(), ct));

    [HttpGet("request")]
    [DelegatedChildCredential(AzoaScopes.DappManage)]
    public async Task<IActionResult> GetRequest([FromQuery] string requestKey, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Respond(await deployments.GetRequestAsync(requestKey, actor, User.GetActingTenantId(), ct));

    [HttpPost("request/cancel")]
    [Authorize(Policy = "DappManage")]
    [RequestSizeLimit(3 * 1024 * 1024)]
    public async Task<IActionResult> CancelRequest(DeploymentRequestCancellation request, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Receipt(await deployments.CancelRequestAsync(request, actor, User.GetActingTenantId(), ct));

    [HttpPost("{id}/continue")]
    [DelegatedChildCredential(AzoaScopes.DappManage, AzoaScopes.ContractDeploySign, AzoaScopes.ContractCallSign, AzoaScopes.SolanaAccountSign)]
    [Authorize(Policy = "DappManage")]
    public async Task<IActionResult> Continue(string id, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Receipt(await deployments.ContinueAsync(id, actor, User.GetActingTenantId(), ct, await CredentialAsync(ct)));

    [HttpPost("{id}/cancel")]
    [DelegatedChildCredential(AzoaScopes.DappManage)]
    [Authorize(Policy = "DappManage")]
    public async Task<IActionResult> Cancel(string id, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Receipt(await deployments.CancelAsync(id, actor, User.GetActingTenantId(), ct));

    [HttpPost("{id}/invoke")]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Invoke(string id, DeploymentInvokeRequest request, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthorized();
        var authority = await RequiredAuthority().CaptureCredentialAsync(User, ct);
        if (authority.IsError || authority.Result is null) return BadRequest(authority);
        return Respond(await deployments.InvokeAsync(id, actor, User.GetActingTenantId(),
            Request.Headers["Idempotency-Key"].ToString(), request, ct, authority.Result));
    }

    [HttpPost("{id}/recovery/original-budget")]
    [Authorize(Policy = "DappManage")]
    public async Task<IActionResult> RecoverWithinBudget(string id, DeploymentOriginalBudgetRecoveryRequest request, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Receipt(await deployments.RecoverWithinBudgetAsync(id, actor, User.GetActingTenantId(), request, ct,
            await CredentialAsync(ct)));

    [HttpPost("{id}/recovery/quote")]
    [Authorize(Policy = "DappManage")]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> QuoteRecovery(string id, DeploymentRecoveryQuoteRequest request, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Respond(await deployments.QuoteRecoveryAsync(id, actor, User.GetActingTenantId(), request, ct));

    [HttpPost("{id}/recovery/start")]
    [Authorize(Policy = "DappManage")]
    [RequestSizeLimit(3 * 1024 * 1024)]
    public async Task<IActionResult> StartRecovery(string id, DeploymentRecoveryStartRequest request, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Receipt(await deployments.StartRecoveryAsync(id, actor, User.GetActingTenantId(), request, ct,
            await CredentialAsync(ct)));

    [HttpPost("{id}/recovery/invalidate")]
    [Authorize(Policy = "DappManage")]
    [RequestSizeLimit(3 * 1024 * 1024)]
    public async Task<IActionResult> InvalidateRecovery(string id, DeploymentRecoveryStartRequest request, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Respond(await deployments.InvalidateRecoveryAsync(id, actor, User.GetActingTenantId(), request, ct));

    [HttpPost("{id}/recovery/ancestry/inspect")]
    [Authorize(Policy = "DappManage")]
    public async Task<IActionResult> InspectRecoveryAncestry(string id, CancellationToken ct)
        => Actor() is not { } actor ? Unauthorized() : Respond(await deployments.InspectRecoveryAncestryAsync(id, actor, User.GetActingTenantId(), ct));

    [HttpGet("{id}/artifact.mjs")]
    public async Task<IActionResult> Loader(string id, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthorized();
        var result = await deployments.GetLoaderAsync(id, actor, User.GetActingTenantId(), ct);
        return result.IsError || result.Result is null ? BadRequest(result)
            : File(Encoding.UTF8.GetBytes(result.Result), "text/javascript; charset=utf-8", "azoa-deployment.mjs");
    }

    /// <summary>Captures the caller's credential; null fails native signing closed in the service (platform runtime needs none).</summary>
    private async Task<SigningCredentialWitness?> CredentialAsync(CancellationToken ct)
    {
        var captured = await RequiredAuthority().CaptureCredentialAsync(User, ct);
        return captured.IsError ? null : captured.Result;
    }

    private SigningAdmissionAuthorityFactory RequiredAuthority()
        => signingAuthority ?? throw new InvalidOperationException("Signing authority capture is not registered.");
    private Guid? Actor() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) ? actor : null;
    private IActionResult Respond<T>(AZOAResult<T> result) => result.IsError ? BadRequest(result) : Ok(result);
    private IActionResult Receipt(AZOAResult<DeploymentReceipt> result) => result.IsError ? BadRequest(result)
        : result.Result?.State is "Confirmed" or "Failed" or "Cancelled" or "AbortedClean" or "FailedClean" ? Ok(result) : Accepted(result);
}
