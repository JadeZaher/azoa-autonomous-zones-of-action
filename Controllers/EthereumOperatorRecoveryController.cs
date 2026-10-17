using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
        if (RecoveryModule(network) is not { } recovery) return Conflict("Ethereum recovery is unavailable.");
        Response.Headers.CacheControl = "no-store";
        return Ok(await recovery.ListPreparationsAsync(Guid.Empty, ct));
    }

    [HttpPost("activate")]
    public async Task<IActionResult> Activate(ChainNetwork network, EthereumPlatformActivationRequest request, CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var actor)) return Unauthorized();
        if (RecoveryModule(network) is not { } recovery) return Conflict("Ethereum recovery is unavailable.");
        var result = await recovery.ActivateSignerAsync(request.Sender, actor, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }

    [HttpPost("{id}")]
    public async Task<IActionResult> Recover(ChainNetwork network, string id, EthereumRecoveryRequest request, CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var actor)) return Unauthorized();
        if (RecoveryModule(network) is not { } recovery) return Conflict("Ethereum recovery is unavailable.");
        var result = await recovery.RecoverPreparationAsync(id, actor, Guid.Empty, true, request.Cancel,
            request.MaximumFeePerGas, request.Reason, AzoaScopes.ValueSigningScopes, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }

    /// <summary>D6: an unconfigured network is a refusal (409), never an unhandled provider-resolution fault.</summary>
    private IEthereumRecoveryModule? RecoveryModule(ChainNetwork network)
    {
        try { return providers.GetProvider("Ethereum", network) as IEthereumRecoveryModule; }
        catch (BlockchainProviderNotFoundException) { return null; }
    }
}
