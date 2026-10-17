using AZOA.WebAPI.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UserNFTController : ControllerBase
{
    private readonly IUserNFTService _userNFTService;
    private readonly INftLifecycleService _lifecycle;
    private readonly AZOA.WebAPI.Services.Signing.SigningAdmissionAuthorityFactory? _signingAuthority;

    public UserNFTController(IUserNFTService userNFTService, INftLifecycleService lifecycle,
        AZOA.WebAPI.Services.Signing.SigningAdmissionAuthorityFactory? signingAuthority = null)
    {
        _userNFTService = userNFTService;
        _lifecycle = lifecycle;
        _signingAuthority = signingAuthority;
    }

    [HttpPost("mint-cost-quote")]
    public async Task<IActionResult> QuoteMintCosts([FromBody] UserNFTMintModel model)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized();
        if (!User.HasSigningScope(AzoaScopes.NftMint)) return Forbid();
        var result = await _lifecycle.QuoteMintCostsAsync(userId.Value, userId.Value, model, ReadIdempotencyKey(),
            HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty);
        return result.IsError || result.Result is null ? BadRequest(result)
            : Ok(AZOAResult<NftProtocolCostOffer>.Success(NftProtocolCostOffer.From(result.Result)));
    }

    [HttpPost("mint")]
    public async Task<IActionResult> MintUserNFT([FromBody] UserNFTMintModel model)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized();
        if (!User.HasSigningScope(AzoaScopes.NftMint)) return Forbid();
        var captured = await (_signingAuthority ?? throw new InvalidOperationException("Signing authority capture is not registered."))
            .CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await _lifecycle.MintAsync(userId.Value, userId.Value, model, ReadIdempotencyKey(),
            HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty, credential: captured.Result);
        return LifecycleResponse(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetUserNFT(Guid id)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _userNFTService.GetUserNFTAsync(id);
        if (result.IsError) return NotFound(result);
        if (result.Result?.UserId != userId.Value) return StatusCode(StatusCodes.Status403Forbidden);

        return Ok(result);
    }

    [HttpGet("by-token/{chainType}/{contractAddress}/{tokenId}")]
    public async Task<IActionResult> GetUserNFTByTokenId(string chainType, string contractAddress, string tokenId)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _userNFTService.GetUserNFTByTokenIdAsync(chainType, contractAddress, tokenId);
        if (result.IsError) return NotFound(result);
        if (result.Result?.UserId != userId.Value) return StatusCode(StatusCodes.Status403Forbidden);

        return Ok(result);
    }

    [HttpGet("user/{userId}")]
    public async Task<IActionResult> GetUserNFTsByUser(Guid userId)
    {
        var callerId = GetUserIdFromClaims();
        if (callerId == null) return Unauthorized();
        if (userId != callerId.Value) return StatusCode(StatusCodes.Status403Forbidden);

        var result = await _userNFTService.GetUserNFTsByUserAsync(userId);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpPost("{id}/transfer")]
    public async Task<IActionResult> TransferUserNFT(Guid id, [FromBody] TransferRequest model)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        if (!User.HasSigningScope(AzoaScopes.TransferSign)) return Forbid();
        var captured = await (_signingAuthority ?? throw new InvalidOperationException("Signing authority capture is not registered."))
            .CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await _lifecycle.MoveAsync(userId.Value, id, model.RecipientAddress, false, ReadIdempotencyKey(),
            HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty, model.FeeAuthorization, credential: captured.Result);
        return LifecycleResponse(result);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> BurnUserNFT(Guid id, [FromQuery] long? feeRevision = null, [FromQuery] string? maxNativeFeeBaseUnits = null, [FromQuery] string? feeQuoteId = null)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        if (!User.HasSigningScope(AzoaScopes.NftBurn)) return Forbid();
        if (feeRevision.HasValue != (maxNativeFeeBaseUnits is not null) || feeRevision.HasValue != (feeQuoteId is not null)) return BadRequest("Fee revision, quote identifier, and maximum native fee are required together.");
        var captured = await (_signingAuthority ?? throw new InvalidOperationException("Signing authority capture is not registered."))
            .CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await _lifecycle.MoveAsync(userId.Value, id, null, true, ReadIdempotencyKey(),
            HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty,
            feeRevision.HasValue ? new NftFeeAuthorization(feeRevision.Value, maxNativeFeeBaseUnits!, feeQuoteId!) : null, credential: captured.Result);
        return LifecycleResponse(result);
    }

    [HttpPost("{userNFTId}/holons/{holonId}/bind")]
    public async Task<IActionResult> BindHolonToUserNFT(Guid userNFTId, Guid holonId, [FromBody] HolonNFTBindingModel model)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _userNFTService.BindHolonToUserNFTAsync(holonId, userNFTId, model, userId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet("{userNFTId}/holons")]
    public async Task<IActionResult> GetHolonBindings(Guid userNFTId)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var nftResult = await _userNFTService.GetUserNFTAsync(userNFTId);
        if (nftResult.IsError) return NotFound(nftResult);
        if (nftResult.Result?.UserId != userId.Value) return StatusCode(StatusCodes.Status403Forbidden);

        var result = await _userNFTService.GetHolonBindingsAsync(userNFTId, callerUserId: userId);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpPut("holons/{bindingId}")]
    public async Task<IActionResult> UpdateHolonBinding(Guid bindingId, [FromBody] HolonNFTBindingUpdateModel model)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _userNFTService.UpdateHolonBindingAsync(bindingId, model, userId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpDelete("holons/{bindingId}")]
    public async Task<IActionResult> RemoveHolonBinding(Guid bindingId)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _userNFTService.RemoveHolonBindingAsync(bindingId, userId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("{userNFTId}/wallets/{walletId}/bind")]
    public async Task<IActionResult> BindWalletToUserNFT(Guid userNFTId, Guid walletId, [FromBody] WalletNFTBindingModel model)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _userNFTService.BindWalletToUserNFTAsync(walletId, userNFTId, model, userId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet("{userNFTId}/wallets")]
    public async Task<IActionResult> GetWalletBindings(Guid userNFTId)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var nftResult = await _userNFTService.GetUserNFTAsync(userNFTId);
        if (nftResult.IsError) return NotFound(nftResult);
        if (nftResult.Result?.UserId != userId.Value) return StatusCode(StatusCodes.Status403Forbidden);

        var result = await _userNFTService.GetWalletBindingsAsync(userNFTId, callerUserId: userId);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpPut("wallets/{bindingId}")]
    public async Task<IActionResult> UpdateWalletBinding(Guid bindingId, [FromBody] WalletNFTBindingUpdateModel model)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _userNFTService.UpdateWalletBindingAsync(bindingId, model, userId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpDelete("wallets/{bindingId}")]
    public async Task<IActionResult> RemoveWalletBinding(Guid bindingId)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _userNFTService.RemoveWalletBindingAsync(bindingId, userId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet("{userNFTId}/composite")]
    public async Task<IActionResult> GetUserNFTComposite(Guid userNFTId)
    {
        var result = await _userNFTService.GetUserNFTCompositeAsync(userNFTId, callerUserId: GetUserIdFromClaims());
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpGet("user/{userId}/composite")]
    public async Task<IActionResult> GetUserNFTCompositesByUser(Guid userId)
    {
        var result = await _userNFTService.GetUserNFTCompositesByUserAsync(userId, callerUserId: GetUserIdFromClaims());
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpPost("verify-ownership")]
    public async Task<IActionResult> VerifyUserNFTOwnership([FromBody] OwnershipVerificationRequest model)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized();
        var result = await _userNFTService.VerifyUserNFTOwnershipAsync(
            userId.Value,
            model.ChainType,
            model.NFTContractAddress,
            model.TokenId
        );
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("verify-holon-access")]
    public async Task<IActionResult> VerifyHolonAccess([FromBody] AccessVerificationRequest model)
    {
        var result = await _userNFTService.VerifyHolonAccessAsync(
            model.UserNFTId,
            model.HolonId,
            model.RequiredPermission, callerUserId: GetUserIdFromClaims()
        );
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("verify-wallet-access")]
    public async Task<IActionResult> VerifyWalletAccess([FromBody] AccessVerificationRequest model)
    {
        var result = await _userNFTService.VerifyWalletAccessAsync(
            model.UserNFTId,
            model.WalletId,
            model.RequiredAccess, callerUserId: GetUserIdFromClaims()
        );
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet("lifecycle")]
    public async Task<IActionResult> ListLifecycle([FromQuery] string? cursor = null, [FromQuery] int limit = 25)
    {
        var actor = GetUserIdFromClaims();
        if (actor is null) return Unauthorized();
        var result = await _lifecycle.ListPendingAsync(actor.Value, cursor, limit, HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet("fee-quote")]
    public async Task<IActionResult> GetFeeQuote([FromQuery] string chainType, [FromQuery] string operation, [FromQuery] Guid? nftId = null, [FromQuery] string? sourceOperationId = null)
    {
        var actor = GetUserIdFromClaims();
        if (actor is null) return Unauthorized();
        var result = await _lifecycle.QuoteFeeAsync(actor.Value, chainType, operation, HttpContext.RequestAborted, nftId, sourceOperationId,
            User.GetActingTenantId() ?? Guid.Empty);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet("lifecycle/request")]
    public async Task<IActionResult> GetRequest([FromQuery] string operation, [FromQuery] string requestKey)
    {
        var actor = GetUserIdFromClaims();
        if (actor is null) return Unauthorized();
        var result = await _lifecycle.GetRequestAsync(actor.Value, operation, requestKey, User.GetActingTenantId() ?? Guid.Empty, HttpContext.RequestAborted);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpPost("lifecycle/request/cancel")]
    public async Task<IActionResult> CancelRequest([FromBody] NftRequestIdentity request)
    {
        var actor = GetUserIdFromClaims();
        if (actor is null) return Unauthorized();
        var result = await _lifecycle.CancelRequestAsync(actor.Value, request.Operation, request.RequestKey, User.GetActingTenantId() ?? Guid.Empty,
            HttpContext.RequestAborted, await CaptureOptionalAsync());
        return result.IsError ? Conflict(result) : Ok(result);
    }

    [HttpPost("lifecycle/{sourceLifecycleId}/refund")]
    public async Task<IActionResult> Refund(string sourceLifecycleId, [FromBody] NftRefundRequest request)
    {
        var actor = GetUserIdFromClaims();
        if (actor is null) return Unauthorized();
        if (!User.HasSigningScope(AzoaScopes.TransferSign)) return Forbid();
        var captured = await (_signingAuthority ?? throw new InvalidOperationException("Signing authority capture is not registered."))
            .CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return LifecycleResponse(await _lifecycle.RefundAsync(actor.Value, sourceLifecycleId, ReadIdempotencyKey(),
            HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty, request.FeeAuthorization, credential: captured.Result));
    }

    [HttpPost("lifecycle/{operationId}/refresh")]
    public async Task<IActionResult> RefreshLifecycle(string operationId)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized();
        return LifecycleResponse(await _lifecycle.RefreshAsync(userId.Value, operationId, false,
            HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty));
    }

    [HttpPost("lifecycle/{operationId}/accept")]
    public async Task<IActionResult> AcceptLifecycle(string operationId, [FromBody] NftStageAcceptance acceptance)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized();
        var current = await _lifecycle.GetAsync(userId.Value, operationId, HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty);
        if (current.IsError || current.Result is null) return NotFound(current);
        var scope = current.Result.Operation switch
        {
            "Burn" => AzoaScopes.NftBurn,
            "Revoke" => AzoaScopes.CredentialRevokeSign,
            "Transfer" or "Refund" => AzoaScopes.TransferSign,
            _ => AzoaScopes.NftMint,
        };
        if (!User.HasSigningScope(scope)) return Forbid();
        var captured = await (_signingAuthority ?? throw new InvalidOperationException("Signing authority capture is not registered."))
            .CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return LifecycleResponse(await _lifecycle.RefreshAsync(userId.Value, operationId, true,
            HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty, captured.Result, acceptance));
    }

    /// <summary>Records the original requester's fresh authority for the reviewed stage; continuing remains a separate call.</summary>
    [HttpPost("lifecycle/{operationId}/renew-authority")]
    public async Task<IActionResult> RenewLifecycleAuthority(string operationId, [FromBody] NftStageAcceptance request)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized();
        var current = await _lifecycle.GetAsync(userId.Value, operationId, HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty);
        if (current.IsError || current.Result is null) return NotFound(current);
        if (!User.HasSigningScope(NftSigningScope.ForOperation(current.Result.Operation))) return Forbid();
        var captured = await (_signingAuthority ?? throw new InvalidOperationException("Signing authority capture is not registered."))
            .CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return LifecycleResponse(await _lifecycle.RenewActorAuthorityAsync(operationId, request, captured.Result, HttpContext.RequestAborted));
    }

    [HttpGet("lifecycle/{operationId}")]
    public async Task<IActionResult> GetLifecycle(string operationId)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized();
        var result = await _lifecycle.GetAsync(userId.Value, operationId, HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    private IActionResult LifecycleResponse(AZOAResult<NftLifecycleView> result)
        => result.IsError ? BadRequest(result) : result.Result?.State is "Completed" or "Cancelled" ? Ok(result) : Accepted(result);

    [HttpPost("lifecycle/{operationId}/cancel")]
    public async Task<IActionResult> CancelLifecycle(string operationId)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized();
        var result = await _lifecycle.CancelAsync(userId.Value, operationId, HttpContext.RequestAborted,
            User.GetActingTenantId() ?? Guid.Empty, await CaptureOptionalAsync());
        return result.IsError ? Conflict(result) : Ok(result);
    }

    /// <summary>Captures the caller's credential when available; only a never-admitted cancellation marker may proceed without one.</summary>
    private async Task<AZOA.WebAPI.Models.Signing.SigningCredentialWitness?> CaptureOptionalAsync()
    {
        if (_signingAuthority is null) return null;
        var captured = await _signingAuthority.CaptureCredentialAsync(User, HttpContext.RequestAborted);
        return captured.IsError ? null : captured.Result;
    }

    private string ReadIdempotencyKey() => Request.Headers["Idempotency-Key"].ToString();

    private Guid? GetUserIdFromClaims()
    {
        // This controller authenticates the user via the "UserId" claim
        // (see MintUserNFT / VerifyUserNFTOwnership) with the standard
        // NameIdentifier/sub subject as a fallback.
        var raw = HttpContext.User.FindFirst("UserId")?.Value
                 ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}

public class TransferRequest
{
    public string RecipientAddress { get; set; } = string.Empty;
    public NftFeeAuthorization? FeeAuthorization { get; set; }
}

public sealed record NftRequestIdentity(string Operation, string RequestKey);
public sealed record NftRefundRequest(NftFeeAuthorization? FeeAuthorization = null);

public class OwnershipVerificationRequest
{
    public string ChainType { get; set; } = string.Empty;
    public string NFTContractAddress { get; set; } = string.Empty;
    public string TokenId { get; set; } = string.Empty;
}

public class AccessVerificationRequest
{
    public Guid UserNFTId { get; set; }
    public Guid HolonId { get; set; }
    public Guid WalletId { get; set; }
    public string RequiredPermission { get; set; } = string.Empty;
    public string RequiredAccess { get; set; } = string.Empty;
}
