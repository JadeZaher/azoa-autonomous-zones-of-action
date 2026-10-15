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
        // Owner-or-public read scope: thread the authenticated user so a non-owner
        // cannot read another tenant's private holon by id.
        var result = await _holonManager.GetAsync(id, GetUserIdFromClaims(), request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<IHolon>>>> Query([FromQuery] HolonQueryRequest query, [FromQuery] AZOARequest? request)
    {
        var result = await _holonManager.QueryAsync(query, GetUserIdFromClaims(), request);
        return Ok(result);
    }

    [HttpPost]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<IHolon>>> Create([FromBody] HolonCreateModel model, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.CreateAsync(model, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<IHolon>>> Update(Guid id, [FromBody] HolonUpdateModel model, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.UpdateAsync(id, model, userId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResponse>> Delete(Guid id, [FromQuery] AZOARequest? request)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.DeleteAsync(id, userId.Value, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new AZOAResponse { Message = "Holon deleted." });
    }

    [HttpPost("{id:guid}/interact")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<IHolon>>> Interact(Guid id, [FromBody] HolonInteractionRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.InteractAsync(id, request, userId.Value, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/mint")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<BlockchainOperationResponse>>> Mint(Guid id, [FromBody] MintRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var result = await _blockchainManager.BuildAndExecuteAsync(builder =>
            builder.ForUser(GetUserIdFromClaims() ?? Guid.Empty)
                   .UsingWallet(request.WalletId)
                   .Mint(request.TokenUri, request.Amount, request.AssetType)
                   .Build(), providerRequest);

        var response = BlockchainOperationResponse.Project(result);
        if (response.IsError) return BadRequest(response);
        return Ok(response);
    }

    [HttpPost("{id:guid}/exchange")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<BlockchainOperationResponse>>> Exchange(Guid id, [FromBody] HolonExchangeRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var result = await _blockchainManager.BuildAndExecuteAsync(builder =>
            builder.ForUser(GetUserIdFromClaims() ?? Guid.Empty)
                   .UsingWallet(request.WalletId)
                   .Exchange(id, request.TargetHolonId, request.ExchangeRate)
                   .Build(), providerRequest);

        var response = BlockchainOperationResponse.Project(result);
        if (response.IsError) return BadRequest(response);
        return Ok(response);
    }

    // ─── Holarchy traversal endpoints — expose the holonic structure ───

    [HttpGet("{id:guid}/children")]
    public async Task<ActionResult<AZOAResult<IEnumerable<IHolon>>>> GetChildren(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _holonManager.GetChildrenAsync(id, GetUserIdFromClaims(), request);
        return Ok(result);
    }

    [HttpGet("{id:guid}/peers")]
    public async Task<ActionResult<AZOAResult<IEnumerable<IHolon>>>> GetPeers(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _holonManager.GetPeersAsync(id, GetUserIdFromClaims(), request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}/ancestors")]
    public async Task<ActionResult<AZOAResult<IEnumerable<IHolon>>>> GetAncestors(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _holonManager.GetAncestorsAsync(id, GetUserIdFromClaims(), request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}/descendants")]
    public async Task<ActionResult<AZOAResult<IEnumerable<IHolon>>>> GetDescendants(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _holonManager.GetDescendantsAsync(id, GetUserIdFromClaims(), request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    // ─── Holonic functionality — operations across the holarchy ───

    [HttpPost("{id:guid}/propagate")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<int>>> Propagate(Guid id, [FromBody] HolonPropagateRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.PropagateAsync(id, request, userId.Value, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}/compose")]
    public async Task<ActionResult<AZOAResult<HolonComposition>>> Compose(Guid id, [FromQuery] AZOARequest? request)
    {
        var result = await _holonManager.ComposeAsync(id, GetUserIdFromClaims(), request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/clone")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<IHolon>>> Clone(Guid id, [FromBody] HolonCloneRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.CloneAsync(id, request, userId.Value, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/move")]
    [Authorize(Policy = "DappDevelop")]
    public async Task<ActionResult<AZOAResult<bool>>> MoveSubtree(Guid id, [FromBody] MoveSubtreeRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IHolon> { IsError = true, Message = "Invalid token." });

        var result = await _holonManager.MoveSubtreeAsync(id, request.NewParentId, userId.Value, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    private Guid? GetUserIdFromClaims()
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

public class HolonExchangeRequest
{
    public Guid WalletId { get; set; }
    public Guid TargetHolonId { get; set; }
    public string ExchangeRate { get; set; } = string.Empty;
}
