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
public class WalletController : ControllerBase
{
    private readonly IWalletManager _walletManager;

    public WalletController(IWalletManager walletManager)
    {
        _walletManager = walletManager;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OASISResult<IWallet>>> Get(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _walletManager.GetAsync(id, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<OASISResult<IEnumerable<IWallet>>>> Query([FromQuery] WalletQueryRequest query, [FromQuery] OASISRequest? request)
    {
        var result = await _walletManager.QueryAsync(query, request);
        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<OASISResult<IWallet>>> Create([FromBody] WalletCreateModel model, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<IWallet> { IsError = true, Message = "Invalid token." });

        var result = await _walletManager.CreateAsync(model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<OASISResult<IWallet>>> Update(Guid id, [FromBody] WalletUpdateModel model, [FromQuery] OASISRequest? request)
    {
        var result = await _walletManager.UpdateAsync(id, model, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<OASISResponse>> Delete(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _walletManager.DeleteAsync(id, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new OASISResponse { Message = "Wallet deleted." });
    }

    [HttpPost("{id:guid}/set-default")]
    public async Task<ActionResult<OASISResult<bool>>> SetDefault(Guid id, [FromQuery] OASISRequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<bool> { IsError = true, Message = "Invalid token." });

        var result = await _walletManager.SetDefaultAsync(avatarId.Value, id, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}/portfolio")]
    public async Task<ActionResult<OASISResult<PortfolioResult>>> GetPortfolio(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _walletManager.GetPortfolioAsync(id, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
