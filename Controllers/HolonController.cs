using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Controllers;

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
    public async Task<ActionResult<OASISResult<IHolon>>> Get(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _holonManager.GetAsync(id, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<OASISResult<IEnumerable<IHolon>>>> Query([FromQuery] HolonQueryRequest query, [FromQuery] OASISRequest? request)
    {
        var result = await _holonManager.QueryAsync(query, request);
        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<OASISResult<IHolon>>> Create([FromBody] HolonCreateModel model, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.CreateAsync(model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<OASISResult<IHolon>>> Update(Guid id, [FromBody] HolonUpdateModel model, [FromQuery] OASISRequest? request)
    {
        var result = await _holonManager.UpdateAsync(id, model, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<OASISResponse>> Delete(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _holonManager.DeleteAsync(id, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new OASISResponse { Message = "Holon deleted." });
    }

    [HttpPost("{id:guid}/interact")]
    public async Task<ActionResult<OASISResult<IHolon>>> Interact(Guid id, [FromBody] HolonInteractionRequest request, [FromQuery] OASISRequest? providerRequest)
    {
        var result = await _holonManager.InteractAsync(id, request, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/mint")]
    public async Task<ActionResult<OASISResult<IBlockchainOperation>>> Mint(Guid id, [FromBody] MintRequest request, [FromQuery] OASISRequest? providerRequest)
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
    public async Task<ActionResult<OASISResult<IBlockchainOperation>>> Exchange(Guid id, [FromBody] ExchangeRequest request, [FromQuery] OASISRequest? providerRequest)
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
    public async Task<ActionResult<OASISResult<IEnumerable<IHolon>>>> GetChildren(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _holonManager.GetChildrenAsync(id, request);
        return Ok(result);
    }

    [HttpGet("{id:guid}/peers")]
    public async Task<ActionResult<OASISResult<IEnumerable<IHolon>>>> GetPeers(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _holonManager.GetPeersAsync(id, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}/ancestors")]
    public async Task<ActionResult<OASISResult<IEnumerable<IHolon>>>> GetAncestors(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _holonManager.GetAncestorsAsync(id, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}/descendants")]
    public async Task<ActionResult<OASISResult<IEnumerable<IHolon>>>> GetDescendants(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _holonManager.GetDescendantsAsync(id, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    // ─── Holonic functionality — operations across the holarchy ───

    [HttpPost("{id:guid}/propagate")]
    public async Task<ActionResult<OASISResult<int>>> Propagate(Guid id, [FromBody] HolonPropagateRequest request, [FromQuery] OASISRequest? providerRequest)
    {
        var result = await _holonManager.PropagateAsync(id, request, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}/compose")]
    public async Task<ActionResult<OASISResult<HolonComposition>>> Compose(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _holonManager.ComposeAsync(id, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/clone")]
    public async Task<ActionResult<OASISResult<IHolon>>> Clone(Guid id, [FromBody] HolonCloneRequest request, [FromQuery] OASISRequest? providerRequest)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.CloneAsync(id, request, avatarId.Value, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/move")]
    public async Task<ActionResult<OASISResult<bool>>> MoveSubtree(Guid id, [FromBody] MoveSubtreeRequest request, [FromQuery] OASISRequest? providerRequest)
    {
        var result = await _holonManager.MoveSubtreeAsync(id, request.NewParentId, providerRequest);
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
    public int Amount { get; set; }
    public string AssetType { get; set; } = string.Empty;
}

public class ExchangeRequest
{
    public Guid WalletId { get; set; }
    public Guid TargetHolonId { get; set; }
    public string ExchangeRate { get; set; } = string.Empty;
}
