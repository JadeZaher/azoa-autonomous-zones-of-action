using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Services.NodeAdmin;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/operator/ethereum/recovery/{network}")]
[Authorize(Policy = "RecentNodeOperatorSession")]
[EnableRateLimiting("financial")]
public sealed class EthereumOperatorRecoveryController(IBlockchainProviderFactory providers) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(ChainNetwork network, CancellationToken ct)
    {
        if (RecoveryModule(network) is not { } recovery) return Unavailable();
        Response.Headers.CacheControl = "no-store";
        return Ok(await recovery.ListPreparationsAsync(Guid.Empty, ct));
    }

    [HttpPost("activate")]
    public async Task<IActionResult> Activate(ChainNetwork network, EthereumPlatformActivationRequest request, CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Unauthorized();
        var actor = operatorActor.SubjectUserId;
        if (RecoveryModule(network) is not { } recovery) return Unavailable();
        var result = await recovery.ActivateSignerAsync(request.Sender, actor, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }

    [HttpPost("{id}")]
    public async Task<IActionResult> Recover(ChainNetwork network, string id, EthereumRecoveryRequest request, CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Unauthorized();
        var actor = operatorActor.SubjectUserId;
        if (RecoveryModule(network) is not { } recovery) return Unavailable();
        var result = await recovery.RecoverPreparationAsync(id, actor, Guid.Empty, true, request.Cancel,
            request.MaximumFeePerGas, request.Reason, AzoaScopes.ValueSigningScopes, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }

    /// <summary>D6: an unconfigured or disabled network is a refusal (409), never an unhandled provider-resolution fault.</summary>
    private IEthereumRecoveryModule? RecoveryModule(ChainNetwork network) => providers.TryResolveEthereumRecovery(network);

    private ConflictObjectResult Unavailable() => Conflict(AZOAResult<bool>.FailureWithCode(
        EthereumRecoveryResolution.UnavailableMessage, EthereumRecoveryResolution.UnavailableCode));
}
