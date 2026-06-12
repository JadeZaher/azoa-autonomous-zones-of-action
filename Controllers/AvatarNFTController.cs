using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AvatarNFTController : ControllerBase
{
    private readonly IAvatarNFTService _avatarNFTService;

    public AvatarNFTController(IAvatarNFTService avatarNFTService)
    {
        _avatarNFTService = avatarNFTService;
    }

    [HttpPost("mint")]
    public async Task<IActionResult> MintAvatarNFT([FromBody] AvatarNFTMintModel model)
    {
        var result = await _avatarNFTService.MintAvatarNFTAsync(
            Guid.Parse(HttpContext.User.FindFirst("AvatarId")?.Value ?? ""),
            model
        );

        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetAvatarNFT(Guid id)
    {
        var result = await _avatarNFTService.GetAvatarNFTAsync(id);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpGet("by-token/{chainType}/{contractAddress}/{tokenId}")]
    public async Task<IActionResult> GetAvatarNFTByTokenId(string chainType, string contractAddress, string tokenId)
    {
        var result = await _avatarNFTService.GetAvatarNFTByTokenIdAsync(chainType, contractAddress, tokenId);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpGet("avatar/{avatarId}")]
    public async Task<IActionResult> GetAvatarNFTsByAvatar(Guid avatarId)
    {
        var result = await _avatarNFTService.GetAvatarNFTsByAvatarAsync(avatarId);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpPost("{id}/transfer")]
    public async Task<IActionResult> TransferAvatarNFT(Guid id, [FromBody] TransferRequest model)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _avatarNFTService.TransferAvatarNFTAsync(id, model.RecipientAddress, avatarId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> BurnAvatarNFT(Guid id)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _avatarNFTService.BurnAvatarNFTAsync(id, avatarId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("{avatarNFTId}/holons/{holonId}/bind")]
    public async Task<IActionResult> BindHolonToAvatarNFT(Guid avatarNFTId, Guid holonId, [FromBody] HolonNFTBindingModel model)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _avatarNFTService.BindHolonToAvatarNFTAsync(holonId, avatarNFTId, model, avatarId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet("{avatarNFTId}/holons")]
    public async Task<IActionResult> GetHolonBindings(Guid avatarNFTId)
    {
        var result = await _avatarNFTService.GetHolonBindingsAsync(avatarNFTId);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpPut("holons/{bindingId}")]
    public async Task<IActionResult> UpdateHolonBinding(Guid bindingId, [FromBody] HolonNFTBindingUpdateModel model)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _avatarNFTService.UpdateHolonBindingAsync(bindingId, model, avatarId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpDelete("holons/{bindingId}")]
    public async Task<IActionResult> RemoveHolonBinding(Guid bindingId)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _avatarNFTService.RemoveHolonBindingAsync(bindingId, avatarId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("{avatarNFTId}/wallets/{walletId}/bind")]
    public async Task<IActionResult> BindWalletToAvatarNFT(Guid avatarNFTId, Guid walletId, [FromBody] WalletNFTBindingModel model)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _avatarNFTService.BindWalletToAvatarNFTAsync(walletId, avatarNFTId, model, avatarId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet("{avatarNFTId}/wallets")]
    public async Task<IActionResult> GetWalletBindings(Guid avatarNFTId)
    {
        var result = await _avatarNFTService.GetWalletBindingsAsync(avatarNFTId);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpPut("wallets/{bindingId}")]
    public async Task<IActionResult> UpdateWalletBinding(Guid bindingId, [FromBody] WalletNFTBindingUpdateModel model)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _avatarNFTService.UpdateWalletBindingAsync(bindingId, model, avatarId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpDelete("wallets/{bindingId}")]
    public async Task<IActionResult> RemoveWalletBinding(Guid bindingId)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _avatarNFTService.RemoveWalletBindingAsync(bindingId, avatarId.Value);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet("{avatarNFTId}/composite")]
    public async Task<IActionResult> GetAvatarNFTComposite(Guid avatarNFTId)
    {
        var result = await _avatarNFTService.GetAvatarNFTCompositeAsync(avatarNFTId);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpGet("avatar/{avatarId}/composite")]
    public async Task<IActionResult> GetAvatarNFTCompositesByAvatar(Guid avatarId)
    {
        var result = await _avatarNFTService.GetAvatarNFTCompositesByAvatarAsync(avatarId);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpPost("verify-ownership")]
    public async Task<IActionResult> VerifyAvatarNFTOwnership([FromBody] OwnershipVerificationRequest model)
    {
        var result = await _avatarNFTService.VerifyAvatarNFTOwnershipAsync(
            Guid.Parse(HttpContext.User.FindFirst("AvatarId")?.Value ?? ""),
            model.ChainType,
            model.NFTContractAddress,
            model.TokenId
        );
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("verify-holon-access")]
    public async Task<IActionResult> VerifyHolonAccess([FromBody] AccessVerificationRequest model)
    {
        var result = await _avatarNFTService.VerifyHolonAccessAsync(
            model.AvatarNFTId,
            model.HolonId,
            model.RequiredPermission
        );
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("verify-wallet-access")]
    public async Task<IActionResult> VerifyWalletAccess([FromBody] AccessVerificationRequest model)
    {
        var result = await _avatarNFTService.VerifyWalletAccessAsync(
            model.AvatarNFTId,
            model.WalletId,
            model.RequiredAccess
        );
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    private Guid? GetAvatarIdFromClaims()
    {
        // This controller authenticates the avatar via the "AvatarId" claim
        // (see MintAvatarNFT / VerifyAvatarNFTOwnership) with the standard
        // NameIdentifier/sub subject as a fallback.
        var raw = HttpContext.User.FindFirst("AvatarId")?.Value
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
    public Guid AvatarNFTId { get; set; }
    public Guid HolonId { get; set; }
    public Guid WalletId { get; set; }
    public string RequiredPermission { get; set; } = string.Empty;
    public string RequiredAccess { get; set; } = string.Empty;
}