using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
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
    private readonly IHolonManager _holonManager;

    public NftController(
        INftManager nftManager,
        IFungibleTokenManager fungibleTokenManager,
        IHolonManager holonManager)
    {
        _nftManager = nftManager;
        _fungibleTokenManager = fungibleTokenManager;
        _holonManager = holonManager;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<NftResult>>> Get(Guid id, [FromQuery] AZOARequest? request)
    {
        // Owner-or-public read scope: a non-owner cannot read another tenant's private
        // NFT by id (manager returns "not found" rather than confirming it).
        var result = await _nftManager.GetAsync(id, GetAvatarIdFromClaims(), request);
        if (result.IsError || result.Result == null) return NotFound(result);

        var nftResult = MapToNftResult(result.Result);
        return Ok(new AZOAResult<NftResult> { Result = nftResult, Message = result.Message });
    }

    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<NftResult>>>> Query([FromQuery] NftQueryRequest query, [FromQuery] AZOARequest? request)
    {
        var result = await _nftManager.QueryAsync(query, GetAvatarIdFromClaims(), request);
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

        if (!User.HasSigningScope(AzoaScopes.NftMint))
            return StatusCode(StatusCodes.Status403Forbidden, new AZOAResult<IBlockchainOperation>
            {
                IsError = true,
                Message = $"Caller lacks the '{AzoaScopes.NftMint}' scope required to mint an NFT."
            });

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

    /// <summary>
    /// One-shot fungible token mint (fungible-mint-and-render-model, §11.3): launch
    /// a fungible ASA with a real supply + decimals into the caller's provisioned
    /// custodial wallet, WITHOUT authoring a quest DAG. The direct parallel to the
    /// <c>FungibleTokenCreate</c> quest node — it shares the SAME manager path
    /// (<see cref="IFungibleTokenManager.CreateAsync"/>), the SAME fail-closed KYC
    /// gate, and the SAME idempotency discipline.
    ///
    /// Idempotency: the optional <c>Idempotency-Key</c> header wins; absent ⇒ the
    /// manager derives a deterministic content key over the token descriptor (never
    /// random). The dedup namespace is partitioned by the API-key id when the caller
    /// is an API-key principal, else by the authenticated avatar id (a stable
    /// per-identity partition, mirroring the quest node's run-id sentinel) so two
    /// avatars reusing the same human-friendly key never collide.
    ///
    /// IDOR: the target is ALWAYS the authenticated avatar; the body carries no
    /// owner id. KYC gate (§11.4): the value-bearing launch is gated in the manager
    /// choke point — a fail-closed rejection carries the <c>KYC_FORBIDDEN:</c> prefix
    /// → 403 (consistent with <c>AllocationController</c> / the <c>Mint</c> route).
    /// </summary>
    [HttpPost("fungible-mint")]
    public async Task<ActionResult<AZOAResult<FungibleTokenResult>>> FungibleMint(
        [FromBody] FungibleMintRequest request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<FungibleTokenResult> { IsError = true, Message = "Invalid token." });

        if (!User.HasSigningScope(AzoaScopes.NftMint))
            return StatusCode(StatusCodes.Status403Forbidden, new AZOAResult<FungibleTokenResult>
            {
                IsError = true,
                Message = $"Caller lacks the '{AzoaScopes.NftMint}' scope required to launch a fungible token."
            });

        // The manager partitions the idempotency namespace by this id. An API-key
        // principal carries an ApiKeyId claim; a JWT self-run (the frontend test
        // harness) does not — fall back to the avatar id so the partition is still
        // stable and per-identity (FungibleTokenCreateNodeHandler run-id precedent).
        var partitionId = GetApiKeyId() ?? avatarId.Value.ToString();

        // Client Idempotency-Key wins; blank ⇒ null ⇒ the manager derives its
        // deterministic content key (never a random per-request key).
        var idempotencyKey = ReadIdempotencyKey();

        // tenant-consent-delegation AC4b: forward the acting tenant (null for a plain
        // self-run) so the platform-signed ASA create runs the live consent gate.
        var actingTenantId = User.GetActingTenantId();

        var create = new FungibleTokenCreateRequest
        {
            ChainType = request.ChainType,
            Name = request.Name,
            UnitName = request.UnitName,
            Total = request.Total,
            Decimals = request.Decimals
        };

        var result = await _fungibleTokenManager.CreateAsync(
            avatarId.Value, create, avatarId.Value, idempotencyKey, partitionId, actingTenantId);

        if (result.IsError || result.Result == null)
        {
            // Fail-closed KYC surfaces a KYC_FORBIDDEN: prefix → 403.
            if (result.Message?.StartsWith(KycAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
                return StatusCode(StatusCodes.Status403Forbidden, result);

            return BadRequest(result);
        }

        // D10 Holon↔asset link (opt-in, mirrors FungibleTokenCreateNodeHandler): copy
        // the created asset id + chain id onto the caller-named holon. A replayed
        // (idempotent) result re-applies the same link harmlessly.
        if (request.HolonId is { } holonId && !result.Result.Replayed)
        {
            var tokenId = string.IsNullOrWhiteSpace(result.Result.AssetId) ? null : result.Result.AssetId;
            var chainId = string.IsNullOrWhiteSpace(request.ChainType) ? null : request.ChainType;

            if (tokenId is not null || chainId is not null)
            {
                var update = new HolonUpdateModel { TokenId = tokenId, ChainId = chainId };
                // Scope the link to the authenticated avatar (IDOR-safe — a holon the
                // caller does not own is not updated).
                var link = await _holonManager.UpdateAsync(holonId, update, avatarId.Value);
                if (link.IsError)
                    return BadRequest(new AZOAResult<FungibleTokenResult>
                    {
                        IsError = true,
                        Message = $"Token launched ({result.Result.AssetId}) but linking holon {holonId} failed: {link.Message}"
                    });
            }
        }

        return Ok(result);
    }

    [HttpPost("{id:guid}/transfer")]
    public async Task<ActionResult<AZOAResult<IBlockchainOperation>>> Transfer(Guid id, [FromBody] NftTransferRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IBlockchainOperation> { IsError = true, Message = "Invalid token." });

        if (!User.HasSigningScope(AzoaScopes.TransferSign))
            return StatusCode(StatusCodes.Status403Forbidden, new AZOAResult<IBlockchainOperation>
            {
                IsError = true,
                Message = $"Caller lacks the '{AzoaScopes.TransferSign}' scope required to transfer an NFT."
            });

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

        // Burn maps to nft:mint per AzoaScopes.OperationScopeMap.
        if (!User.HasSigningScope(AzoaScopes.NftMint))
            return StatusCode(StatusCodes.Status403Forbidden, new AZOAResult<IBlockchainOperation>
            {
                IsError = true,
                Message = $"Caller lacks the '{AzoaScopes.NftMint}' scope required to burn an NFT."
            });

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

    /// <summary>
    /// Reads the optional client <c>Idempotency-Key</c> header (mirrors
    /// <c>AllocationController.ReadIdempotencyKey</c>). Returns null when
    /// absent/blank so the manager falls back to its deterministic content key.
    /// </summary>
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
