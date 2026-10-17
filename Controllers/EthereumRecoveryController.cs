using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/ethereum/recovery/{network}")]
[Authorize(Policy = "FirstPartyLogin")]
[EnableRateLimiting("financial")]
public sealed class EthereumRecoveryController(IBlockchainProviderFactory providers, IWalletStore wallets) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(ChainNetwork network, CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var actor)) return Unauthorized();
        if (RecoveryModule(network) is not { } recovery) return Conflict("Ethereum recovery is unavailable.");
        Response.Headers.CacheControl = "no-store";
        return Ok(await recovery.ListPreparationsAsync(actor, ct));
    }

    [HttpPost("activate")]
    public async Task<IActionResult> Activate(ChainNetwork network, EthereumSignerActivationRequest request, CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var actor)) return Unauthorized();
        var wallet = await wallets.GetByIdAsync(request.WalletId, ct);
        if (wallet.IsError || wallet.Result is null || wallet.Result.UserId != actor || wallet.Result.ChainType != "Ethereum") return NotFound();
        if (RecoveryModule(network) is not { } recovery) return Conflict("Ethereum recovery is unavailable.");
        var result = await recovery.ActivateSignerAsync(wallet.Result.Address, actor, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }

    [HttpPost("{id}")]
    public async Task<IActionResult> Recover(ChainNetwork network, string id, EthereumRecoveryRequest request, CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var actor)) return Unauthorized();
        if (RecoveryModule(network) is not { } recovery) return Conflict("Ethereum recovery is unavailable.");
        var scopes = AzoaScopes.ValueSigningScopes.Append(AzoaScopes.NftMint).Where(User.HasSigningScope).ToHashSet(StringComparer.Ordinal);
        var result = await recovery.RecoverPreparationAsync(id, actor, User.GetActingTenantId() ?? Guid.Empty,
            false, request.Cancel, request.MaximumFeePerGas, request.Reason, scopes, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }

    /// <summary>D6: an unconfigured network is a refusal (409), never an unhandled provider-resolution fault.</summary>
    private IEthereumRecoveryModule? RecoveryModule(ChainNetwork network)
    {
        try { return providers.GetProvider("Ethereum", network) as IEthereumRecoveryModule; }
        catch (BlockchainProviderNotFoundException) { return null; }
    }
}
