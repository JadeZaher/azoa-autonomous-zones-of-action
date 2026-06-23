using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NftController : ControllerBase
{
    private readonly INftManager _nftManager;

    public NftController(INftManager nftManager)
    {
        _nftManager = nftManager;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<NftResult>>> Get(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _nftManager.GetAsync(id, request);
        if (result.IsError || result.Result == null) return NotFound(result);

        var nftResult = MapToNftResult(result.Result);
        return Ok(new AZOAResult<NftResult> { Result = nftResult, Message = result.Message });
    }

    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<NftResult>>>> Query([FromQuery] NftQueryRequest query, [FromQuery] AZOARequest? request)
    {
        var result = await _nftManager.QueryAsync(query, request);
        if (result.IsError || result.Result == null) return Ok(new AZOAResult<IEnumerable<NftResult>> { IsError = true, Message = result.Message });

        var mapped = result.Result.Select(MapToNftResult).ToList();
        return Ok(new AZOAResult<IEnumerable<NftResult>> { Result = mapped, Message = "Success" });
    }

    [HttpPost("mint")]
    public async Task<ActionResult<AZOAResult<IBlockchainOperation>>> Mint([FromBody] NftMintRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IBlockchainOperation> { IsError = true, Message = "Invalid token." });

        var result = await _nftManager.MintAsync(request, avatarId.Value, providerRequest);
        if (!result.IsError) return Ok(result);

        // value-path-wiring H3: the KYC gate now lives in NftManager.MintAsync. A
        // fail-closed KYC rejection carries the KYC_FORBIDDEN: prefix → 403,
        // consistent with the kyc-module convention (AllocationController.cs:80,
        // KycController.cs:124). Any other error stays 400.
        if (result.Message?.StartsWith(KycAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
            return StatusCode(StatusCodes.Status403Forbidden, result);

        return BadRequest(result);
    }

    [HttpPost("{id:guid}/transfer")]
    public async Task<ActionResult<AZOAResult<IBlockchainOperation>>> Transfer(Guid id, [FromBody] NftTransferRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IBlockchainOperation> { IsError = true, Message = "Invalid token." });

        var result = await _nftManager.TransferAsync(id, request, avatarId.Value, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/burn")]
    public async Task<ActionResult<AZOAResult<IBlockchainOperation>>> Burn(Guid id, [FromBody] NftBurnRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IBlockchainOperation> { IsError = true, Message = "Invalid token." });

        var result = await _nftManager.BurnAsync(id, request.WalletId, avatarId.Value, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}/metadata")]
    [AllowAnonymous]
    public async Task<ActionResult<AZOAResult<NftMetadata>>> GetMetadata(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _nftManager.GetMetadataAsync(id, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    private NftResult MapToNftResult(INft holon)
    {
        var metadata = new NftMetadata
        {
            Name = holon.Name,
            Description = holon.Description
        };

        if (holon.Metadata != null)
        {
            if (holon.Metadata.TryGetValue("image", out var image)) metadata.Image = image;
            if (holon.Metadata.TryGetValue("external_url", out var extUrl)) metadata.ExternalUrl = extUrl;
            if (holon.Metadata.TryGetValue("animation_url", out var animUrl)) metadata.AnimationUrl = animUrl;
        }

        return new NftResult
        {
            Id = holon.Id,
            Name = holon.Name,
            Description = holon.Description,
            OwnerAvatarId = holon.AvatarId,
            ChainId = holon.ChainId ?? string.Empty,
            TokenId = holon.TokenId,
            Metadata = metadata,
            CreatedDate = holon.CreatedDate,
            ModifiedDate = holon.ModifiedDate,
            IsActive = holon.IsActive
        };
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
