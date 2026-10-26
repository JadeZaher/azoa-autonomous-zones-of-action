using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
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
    private readonly IFungibleTokenManager _fungibleTokenManager;
    private readonly AZOA.WebAPI.Services.Signing.SigningAdmissionAuthorityFactory? _signingAuthority;

    public NftController(
        INftManager nftManager,
        IFungibleTokenManager fungibleTokenManager,
        AZOA.WebAPI.Services.Signing.SigningAdmissionAuthorityFactory? signingAuthority = null)
    {
        _nftManager = nftManager;
        _fungibleTokenManager = fungibleTokenManager;
        _signingAuthority = signingAuthority;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<NftResult>>> Get(Guid id, [FromQuery] AZOARequest? request)
    {
        // Reads require proven current ownership.
        var result = await _nftManager.GetAsync(id, GetUserIdFromClaims(), request);
        if (result.IsError || result.Result == null) return NotFound(result);

        var nftResult = MapToNftResult(result.Result);
        return Ok(new AZOAResult<NftResult> { Result = nftResult, Message = result.Message });
    }

    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<NftResult>>>> Query([FromQuery] NftQueryRequest query, [FromQuery] AZOARequest? request)
    {
        var result = await _nftManager.QueryAsync(query, GetUserIdFromClaims(), request);
        if (result.IsError || result.Result == null) return Ok(new AZOAResult<IEnumerable<NftResult>> { IsError = true, Message = result.Message });

        var mapped = result.Result.Select(MapToNftResult).ToList();
        return Ok(new AZOAResult<IEnumerable<NftResult>> { Result = mapped, Message = "Success" });
    }

    [HttpPost("mint-cost-quote")]
    public async Task<IActionResult> QuoteMintCosts([FromBody] NftMintRequest request,
        [FromServices] INftLifecycleService lifecycle, [FromQuery] AZOARequest? providerRequest)
    {
        var actor = GetUserIdFromClaims();
        if (actor is null) return Unauthorized();
        if (!User.HasSigningScope(AzoaScopes.NftMint)) return Forbid();
        var model = NftLifecycleOperationProjection.MintModel(request, providerRequest?.NftFeeAuthorization);
        var result = await lifecycle.QuoteMintCostsAsync(actor.Value, actor.Value, model, ReadIdempotencyKey() ?? string.Empty,
            HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty);
        return result.IsError || result.Result is null ? BadRequest(result)
            : Ok(AZOAResult<NftProtocolCostOffer>.Success(NftProtocolCostOffer.From(result.Result)));
    }

    [HttpPost("mint")]
    public async Task<ActionResult<AZOAResult<BlockchainOperationResponse>>> Mint([FromBody] NftMintRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<BlockchainOperationResponse> { IsError = true, Message = "Invalid token." });

        if (!User.HasSigningScope(AzoaScopes.NftMint))
            return StatusCode(StatusCodes.Status403Forbidden, new AZOAResult<BlockchainOperationResponse>
            {
                IsError = true,
                Message = $"Caller lacks the '{AzoaScopes.NftMint}' scope required to mint an NFT."
            });

        providerRequest ??= new AZOARequest();
        providerRequest.IdempotencyKey = ReadIdempotencyKey();
        var captured = await (_signingAuthority ?? throw new InvalidOperationException("Signing authority capture is not registered."))
            .CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        providerRequest.AuthenticationWitness = captured.Result;
        var result = await _nftManager.MintAsync(request, userId.Value, providerRequest, User.GetActingTenantId());
        var response = BlockchainOperationResponse.Project(result);
        if (result.Code == "NFT_PENDING") return Accepted(response);
        if (!response.IsError) return Ok(response);

        if (result.Message?.StartsWith(KycAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
            return StatusCode(StatusCodes.Status403Forbidden, response);

        return BadRequest(response);
    }

    /// <summary>Admits a fungible creation for explicit quote acceptance and durable recovery.</summary>
    [HttpPost("fungible-mint")]
    public async Task<ActionResult<AZOAResult<FungibleTokenResult>>> FungibleMint([FromBody] FungibleMintRequest request)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized();
        if (!User.HasSigningScope(AzoaScopes.TokenCreateSign)) return Forbid();
        var authority = await (_signingAuthority ?? throw new InvalidOperationException("Signing authority capture is not registered."))
            .CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (authority.IsError || authority.Result is null) return BadRequest(AZOAResult<FungibleTokenResult>.Failure(authority.Message));
        request.AuthenticationWitness = authority.Result;
        request.ReserveOwnerTenantId = User.GetActingTenantOrPrincipalTenantId(); // L1.1: the partitions door's tenant, from the principal.
        var result = await _fungibleTokenManager.CreateAsync(userId.Value, request, userId.Value,
            ReadIdempotencyKey(), GetApiKeyId() ?? userId.Value.ToString("N"), User.GetActingTenantId());
        if (result.IsError || result.Result is null) return BadRequest(result);
        return result.Result.Operation?.State == "Completed" ? Ok(result) : Accepted(result);
    }
    [HttpPost("{id:guid}/transfer")]
    public async Task<ActionResult<AZOAResult<BlockchainOperationResponse>>> Transfer(Guid id, [FromBody] NftTransferRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<BlockchainOperationResponse> { IsError = true, Message = "Invalid token." });

        if (!User.HasSigningScope(AzoaScopes.TransferSign))
            return StatusCode(StatusCodes.Status403Forbidden, new AZOAResult<BlockchainOperationResponse>
            {
                IsError = true,
                Message = $"Caller lacks the '{AzoaScopes.TransferSign}' scope required to transfer an NFT."
            });

        providerRequest ??= new AZOARequest();
        providerRequest.IdempotencyKey = ReadIdempotencyKey();
        var captured = await (_signingAuthority ?? throw new InvalidOperationException("Signing authority capture is not registered."))
            .CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        providerRequest.AuthenticationWitness = captured.Result;
        var result = await _nftManager.TransferAsync(id, request, userId.Value, providerRequest, User.GetActingTenantId());
        var response = BlockchainOperationResponse.Project(result);
        if (result.Code == "NFT_PENDING") return Accepted(response);
        if (response.IsError) return BadRequest(response);
        return Ok(response);
    }

    [HttpPost("{id:guid}/burn")]
    public async Task<ActionResult<AZOAResult<BlockchainOperationResponse>>> Burn(Guid id, [FromBody] NftBurnRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<BlockchainOperationResponse> { IsError = true, Message = "Invalid token." });

        // Destruction requires its own signing scope.
        if (!User.HasSigningScope(AzoaScopes.NftBurn))
            return StatusCode(StatusCodes.Status403Forbidden, new AZOAResult<BlockchainOperationResponse>
            {
                IsError = true,
                Message = $"Caller lacks the '{AzoaScopes.NftBurn}' scope required to burn an NFT."
            });

        providerRequest ??= new AZOARequest();
        providerRequest.IdempotencyKey = ReadIdempotencyKey();
        providerRequest.NftFeeAuthorization = request.FeeAuthorization ?? providerRequest.NftFeeAuthorization;
        var captured = await (_signingAuthority ?? throw new InvalidOperationException("Signing authority capture is not registered."))
            .CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        providerRequest.AuthenticationWitness = captured.Result;
        var result = await _nftManager.BurnAsync(id, request.WalletId, userId.Value, providerRequest, User.GetActingTenantId());
        var response = BlockchainOperationResponse.Project(result);
        if (result.Code == "NFT_PENDING") return Accepted(response);
        if (response.IsError) return BadRequest(response);
        return Ok(response);
    }

    [HttpGet("{id:guid}/metadata")]
    public async Task<ActionResult<AZOAResult<NftMetadata>>> GetMetadata(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _nftManager.GetMetadataAsync(id, request, GetUserIdFromClaims());
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
            OwnerUserId = holon.UserId,
            ChainId = holon.ChainId ?? string.Empty,
            TokenId = holon.TokenId,
            Metadata = metadata,
            CreatedDate = holon.CreatedDate,
            ModifiedDate = holon.ModifiedDate,
            IsActive = holon.IsActive
        };
    }

    private Guid? GetUserIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    /// <summary>Reads the caller-provided durable request key.</summary>
    private string? ReadIdempotencyKey()
    {
        if (Request.Headers.TryGetValue("Idempotency-Key", out var values))
        {
            var key = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(key))
                return key.Trim();
        }
        return null;
    }

    private string? GetApiKeyId()
    {
        var value = User.FindFirst("ApiKeyId")?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
