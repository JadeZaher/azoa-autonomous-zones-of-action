using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
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
public class WalletController : ControllerBase
{
    private readonly IWalletManager _walletManager;
    private readonly ILogger<WalletController> _logger;

    public WalletController(
        IWalletManager walletManager,
        ILogger<WalletController>? logger = null)
    {
        _walletManager = walletManager;
        _logger = logger ?? NullLogger<WalletController>.Instance;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<IWallet>>> Get(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IWallet> { IsError = true, Message = "Invalid token." });

        var result = await _walletManager.GetAsync(id, avatarId.Value, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<IWallet>>>> Query([FromQuery] WalletQueryRequest query, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IEnumerable<IWallet>> { IsError = true, Message = "Invalid token." });

        var result = await _walletManager.QueryAsync(query, avatarId.Value, request);
        return Ok(result);
    }

    [HttpPost]
    [Authorize(Policy = "FirstPartyLogin")]
    public async Task<ActionResult<AZOAResult<IWallet>>> Create([FromBody] WalletCreateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IWallet> { IsError = true, Message = "Invalid token." });

        var result = await _walletManager.CreateAsync(model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "FirstPartyLogin")]
    public async Task<ActionResult<AZOAResult<IWallet>>> Update(Guid id, [FromBody] WalletUpdateModel model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IWallet> { IsError = true, Message = "Invalid token." });

        var result = await _walletManager.UpdateAsync(id, model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "FirstPartyLogin")]
    public async Task<ActionResult<AZOAResponse>> Delete(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<bool> { IsError = true, Message = "Invalid token." });

        var result = await _walletManager.DeleteAsync(id, avatarId.Value, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new AZOAResponse { Message = "Wallet deleted." });
    }

    [HttpPost("{id:guid}/set-default")]
    [Authorize(Policy = "FirstPartyLogin")]
    public async Task<ActionResult<AZOAResult<bool>>> SetDefault(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<bool> { IsError = true, Message = "Invalid token." });

        var result = await _walletManager.SetDefaultAsync(avatarId.Value, id, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("{id:guid}/portfolio")]
    public async Task<ActionResult<AZOAResult<PortfolioResult>>> GetPortfolio(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<PortfolioResult> { IsError = true, Message = "Invalid token." });

        var result = await _walletManager.GetPortfolioAsync(id, avatarId.Value, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    // ─── Generate a new wallet on-platform ───

    [HttpPost("generate")]
    [Authorize(Policy = "FirstPartyLogin")]
    public async Task<ActionResult<AZOAResult<IWallet>>> Generate([FromBody] WalletGenerateRequest model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IWallet> { IsError = true, Message = "Invalid token." });

        var result = await _walletManager.GenerateWalletAsync(model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Connect an external wallet (MetaMask, Ghost, etc.) ───

    [HttpPost("connect")]
    [Authorize(Policy = "FirstPartyLogin")]
    public async Task<ActionResult<AZOAResult<IWallet>>> Connect([FromBody] WalletConnectRequest model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IWallet> { IsError = true, Message = "Invalid token." });

        var result = await _walletManager.ConnectWalletAsync(model, avatarId.Value, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    // ─── Export a platform wallet's private key ───

    [HttpPost("{id:guid}/export")]
    [Authorize(Policy = "RecentFirstPartyLogin")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<WalletExportResult>>> Export(Guid id, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<WalletExportResult> { IsError = true, Message = "Invalid token." });

        // See Controllers/AGENTS.md § tenant credential and recovery boundaries.
        if (User.GetActingTenantId() is not null
            || string.Equals(User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                User.FindFirst(AzoaClaims.TokenUse)?.Value,
                AzoaClaims.TokenUseLogin,
                StringComparison.Ordinal))
        {
            return Forbid();
        }

        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        _logger.LogWarning(
            "Sensitive wallet recovery requested by avatar {AvatarId} for wallet {WalletId}.",
            avatarId.Value,
            id);
        var result = await _walletManager.ExportWalletAsync(id, avatarId.Value, request);
        if (result.IsError)
        {
            _logger.LogWarning(
                "Sensitive wallet recovery failed for avatar {AvatarId} and wallet {WalletId}.",
                avatarId.Value,
                id);
            return BadRequest(result);
        }
        _logger.LogInformation(
            "Sensitive wallet recovery completed for avatar {AvatarId} and wallet {WalletId}.",
            avatarId.Value,
            id);
        return Ok(result);
    }

    // ─── Top-up a wallet with test tokens (faucet) — dev / test networks only ───

    [HttpPost("{id:guid}/topup")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<object>>> TopUp(Guid id, [FromBody] WalletTopUpRequest? model, [FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<object> { IsError = true, Message = "Invalid token." });

        if (!User.HasSigningScope(AzoaScopes.WalletManage))
            return StatusCode(StatusCodes.Status403Forbidden, new AZOAResult<object>
            {
                IsError = true,
                Message = $"Caller lacks the '{AzoaScopes.WalletManage}' scope required to top up a wallet."
            });

        // Client-supplied idempotency key (optional). When present, the faucet
        // uses it verbatim so a retried POST /topup dispenses exactly once.
        // When absent the lower layers derive a deterministic content key —
        // absence is still safe; NO random per-request key is generated here.
        var idempotencyKey = ReadIdempotencyKey();

        var result = await _walletManager.TopUpAsync(id, model?.Amount, avatarId.Value, request, idempotencyKey);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Reads the optional client <c>Idempotency-Key</c> request header.
    /// Returns null when absent/blank so the server falls back to its
    /// deterministic content-addressed key (never a random per-request key).
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

    // ─── Get all wallets grouped by type (for UI) ───

    [HttpGet("types")]
    public async Task<ActionResult<AZOAResult<object>>> GetByType([FromQuery] AZOARequest? request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<object> { IsError = true, Message = "Invalid token." });

        var allResult = await _walletManager.QueryAsync(new WalletQueryRequest { AvatarId = avatarId }, avatarId.Value, request);
        if (allResult.IsError || allResult.Result == null)
            return Ok(new AZOAResult<object> { Result = new { external = new List<IWallet>(), platform = new List<IWallet>() } });

        var all = allResult.Result.ToList();
        var external = all.Where(w => w.WalletType == WalletType.External).ToList();
        var platform = all.Where(w => w.WalletType == WalletType.Platform).ToList();

        return Ok(new AZOAResult<object>
        {
            Result = new { external, platform, total = all.Count },
            Message = "Wallets grouped by type."
        });
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

}
