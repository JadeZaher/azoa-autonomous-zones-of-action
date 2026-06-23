using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class HolonController : ControllerBase
{
    private readonly IHolonManager _holonManager;
    private readonly IBlockchainOperationManager _blockchainManager;

    public HolonController(IHolonManager holonManager, IBlockchainOperationManager blockchainManager)
    {
        _holonManager = holonManager;
        _blockchainManager = blockchainManager;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<IHolon>>> Get(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _holonManager.GetAsync(id, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<IHolon>>>> Query([FromQuery] HolonQueryRequest query, [FromQuery] AZOARequest? request)
    {
        var result = await _holonManager.QueryAsync(query, request);
        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<AZOAResult<IHolon>>> Create([FromBody] HolonCreateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.CreateAsync(model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AZOAResult<IHolon>>> Update(Guid id, [FromBody] HolonUpdateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.UpdateAsync(id, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<AZOAResponse>> Delete(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.DeleteAsync(id, avatarId.Value, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new AZOAResponse { Message = "Holon deleted." });
    }

    [HttpPost("{id:guid}/interact")]
    public async Task<ActionResult<AZOAResult<IHolon>>> Interact(Guid id, [FromBody] HolonInteractionRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.InteractAsync(id, request, avatarId.Value, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/mint")]
    public async Task<ActionResult<AZOAResult<IBlockchainOperation>>> Mint(Guid id, [FromBody] MintRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var result = await _blockchainManager.BuildAndExecuteAsync(builder =>
            builder.ForAvatar(GetAvatarIdFromClaims() ?? Guid.Empty)
                   .UsingWallet(request.WalletId)
                   .Mint(request.TokenUri, request.Amount, request.AssetType)
                   .Build(), providerRequest);

        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/exchange")]
    public async Task<ActionResult<AZOAResult<IBlockchainOperation>>> Exchange(Guid id, [FromBody] ExchangeRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var result = await _blockchainManager.BuildAndExecuteAsync(builder =>
            builder.ForAvatar(GetAvatarIdFromClaims() ?? Guid.Empty)
                   .UsingWallet(request.WalletId)
                   .Exchange(id, request.TargetHolonId, request.ExchangeRate)
                   .Build(), providerRequest);

        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Holarchy traversal endpoints — expose the holonic structure ───

    [HttpGet("{id:guid}/children")]
    public async Task<ActionResult<AZOAResult<IEnumerable<IHolon>>>> GetChildren(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _holonManager.GetChildrenAsync(id, request);
        return Ok(result);
    }

    [HttpGet("{id:guid}/peers")]
    public async Task<ActionResult<AZOAResult<IEnumerable<IHolon>>>> GetPeers(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _holonManager.GetPeersAsync(id, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}/ancestors")]
    public async Task<ActionResult<AZOAResult<IEnumerable<IHolon>>>> GetAncestors(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _holonManager.GetAncestorsAsync(id, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}/descendants")]
    public async Task<ActionResult<AZOAResult<IEnumerable<IHolon>>>> GetDescendants(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _holonManager.GetDescendantsAsync(id, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    // ─── Holonic functionality — operations across the holarchy ───

    [HttpPost("{id:guid}/propagate")]
    public async Task<ActionResult<AZOAResult<int>>> Propagate(Guid id, [FromBody] HolonPropagateRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.PropagateAsync(id, request, avatarId.Value, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}/compose")]
    public async Task<ActionResult<AZOAResult<HolonComposition>>> Compose(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _holonManager.ComposeAsync(id, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/clone")]
    public async Task<ActionResult<AZOAResult<IHolon>>> Clone(Guid id, [FromBody] HolonCloneRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.CloneAsync(id, request, avatarId.Value, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/move")]
    public async Task<ActionResult<AZOAResult<bool>>> MoveSubtree(Guid id, [FromBody] MoveSubtreeRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.MoveSubtreeAsync(id, request.NewParentId, avatarId.Value, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}

public class MintRequest
{
    public Guid WalletId { get; set; }
    public string TokenUri { get; set; } = string.Empty;
    public ulong Amount { get; set; }
    public string AssetType { get; set; } = string.Empty;
}

public class ExchangeRequest
{
    public Guid WalletId { get; set; }
    public Guid TargetHolonId { get; set; }
    public string ExchangeRate { get; set; } = string.Empty;
}
