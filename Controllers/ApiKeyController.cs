using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ApiKeyController : ControllerBase
{
    private readonly IApiKeyStore _store;

    public ApiKeyController(IApiKeyStore store)
    {
        _store = store;
    }

    private Guid GetAvatarId()
    {
        var claim = User.FindFirst("AvatarId")?.Value
                 ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
    }

    /// <summary>
    /// Create a new API key. The raw key is returned ONCE — store it securely.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest request)
    {
        var avatarId = GetAvatarId();
        if (avatarId == Guid.Empty)
            return Unauthorized(new AZOAResult<object> { IsError = true, Message = "Avatar not authenticated." });

        var rawKey = ApiKeyAuthenticationHandler.GenerateRawKey();
        var keyHash = ApiKeyAuthenticationHandler.HashKey(rawKey);

        var apiKey = new ApiKey
        {
            AvatarId = avatarId,
            Name = request.Name,
            KeyHash = keyHash,
            KeyPrefix = rawKey[..16],
            ExpiresAt = request.ExpiresInDays.HasValue
                ? DateTime.UtcNow.AddDays(request.ExpiresInDays.Value)
                : null,
            Scopes = request.Scopes,
        };

        await _store.CreateAsync(apiKey, HttpContext.RequestAborted);

        return Ok(new AZOAResult<CreateApiKeyResponse>
        {
            IsError = false,
            Message = "API key created. Store the key securely — it will not be shown again.",
            Result = new CreateApiKeyResponse
            {
                Id = apiKey.Id,
                Name = apiKey.Name,
                Key = rawKey,
                KeyPrefix = apiKey.KeyPrefix,
                ExpiresAt = apiKey.ExpiresAt,
                Scopes = apiKey.Scopes,
                CreatedDate = apiKey.CreatedDate,
            }
        });
    }

    /// <summary>
    /// List all API keys for the authenticated avatar (keys are never shown, only prefixes).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var avatarId = GetAvatarId();
        if (avatarId == Guid.Empty)
            return Unauthorized(new AZOAResult<object> { IsError = true, Message = "Avatar not authenticated." });

        var owned = await _store.ListByAvatarAsync(avatarId, HttpContext.RequestAborted);

        var keys = owned.Select(k => new ApiKeyInfo
        {
            Id = k.Id,
            Name = k.Name,
            KeyPrefix = k.KeyPrefix,
            CreatedDate = k.CreatedDate,
            ExpiresAt = k.ExpiresAt,
            LastUsedAt = k.LastUsedAt,
            RevokedAt = k.RevokedAt,
            IsActive = k.IsActive,
            Scopes = k.Scopes,
        }).ToList();

        return Ok(new AZOAResult<List<ApiKeyInfo>> { IsError = false, Message = "OK", Result = keys });
    }

    /// <summary>
    /// Revoke an API key (soft-delete — key is deactivated, not removed).
    /// </summary>
    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var avatarId = GetAvatarId();
        if (avatarId == Guid.Empty)
            return Unauthorized(new AZOAResult<object> { IsError = true, Message = "Avatar not authenticated." });

        var ok = await _store.RevokeAsync(id, avatarId, DateTime.UtcNow, HttpContext.RequestAborted);
        if (!ok)
            return NotFound(new AZOAResult<object> { IsError = true, Message = "API key not found." });

        return Ok(new AZOAResult<object> { IsError = false, Message = "API key revoked." });
    }

    /// <summary>
    /// Permanently delete an API key record.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var avatarId = GetAvatarId();
        if (avatarId == Guid.Empty)
            return Unauthorized(new AZOAResult<object> { IsError = true, Message = "Avatar not authenticated." });

        var ok = await _store.DeleteAsync(id, avatarId, HttpContext.RequestAborted);
        if (!ok)
            return NotFound(new AZOAResult<object> { IsError = true, Message = "API key not found." });

        return Ok(new AZOAResult<object> { IsError = false, Message = "API key deleted." });
    }
}

// ─── Request / Response DTOs ───

public class CreateApiKeyRequest
{
    public string Name { get; set; } = string.Empty;
    public int? ExpiresInDays { get; set; }
    public string? Scopes { get; set; }
}

public class CreateApiKeyResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// The raw API key — shown only once at creation time.
    /// </summary>
    public string Key { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
    public string? Scopes { get; set; }
    public DateTime CreatedDate { get; set; }
}

public class ApiKeyInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public bool IsActive { get; set; }
    public string? Scopes { get; set; }
}
