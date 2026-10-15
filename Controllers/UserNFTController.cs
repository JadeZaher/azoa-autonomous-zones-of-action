using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UserNFTController : ControllerBase
{
    private readonly IUserNFTService _userNFTService;

    public UserNFTController(IUserNFTService userNFTService)
    {
        _userNFTService = userNFTService;
    }

    [HttpPost("mint")]
    public async Task<IActionResult> MintUserNFT([FromBody] UserNFTMintModel model)
    {
        var result = await _userNFTService.MintUserNFTAsync(
            Guid.Parse(HttpContext.User.FindFirst("UserId")?.Value ?? ""),
            model
        );

        return result.IsError ? BadRequest(result) : Ok(result);
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

        var result = await _userNFTService.TransferUserNFTAsync(id, model.RecipientAddress, userId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> BurnUserNFT(Guid id)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null) return Unauthorized();

        var result = await _userNFTService.BurnUserNFTAsync(id, userId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
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

        var result = await _userNFTService.GetHolonBindingsAsync(userNFTId);
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

        var result = await _userNFTService.GetWalletBindingsAsync(userNFTId);
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
        var result = await _userNFTService.GetUserNFTCompositeAsync(userNFTId);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpGet("user/{userId}/composite")]
    public async Task<IActionResult> GetUserNFTCompositesByUser(Guid userId)
    {
        var result = await _userNFTService.GetUserNFTCompositesByUserAsync(userId);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpPost("verify-ownership")]
    public async Task<IActionResult> VerifyUserNFTOwnership([FromBody] OwnershipVerificationRequest model)
    {
        var result = await _userNFTService.VerifyUserNFTOwnershipAsync(
            Guid.Parse(HttpContext.User.FindFirst("UserId")?.Value ?? ""),
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
            model.RequiredPermission
        );
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("verify-wallet-access")]
    public async Task<IActionResult> VerifyWalletAccess([FromBody] AccessVerificationRequest model)
    {
        var result = await _userNFTService.VerifyWalletAccessAsync(
            model.UserNFTId,
            model.WalletId,
            model.RequiredAccess
        );
        return result.IsError ? BadRequest(result) : Ok(result);
    }

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
}

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