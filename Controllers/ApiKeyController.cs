using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ApiKeyController : ControllerBase
{
    private readonly OASISDbContext _db;

    public ApiKeyController(OASISDbContext db)
    {
        _db = db;
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
            return Unauthorized(new OASISResult<object> { IsError = true, Message = "Avatar not authenticated." });

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

        _db.ApiKeys.Add(apiKey);
        await _db.SaveChangesAsync();

        return Ok(new OASISResult<CreateApiKeyResponse>
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
            return Unauthorized(new OASISResult<object> { IsError = true, Message = "Avatar not authenticated." });

        var keys = await _db.ApiKeys
            .Where(k => k.AvatarId == avatarId)
            .OrderByDescending(k => k.CreatedDate)
            .Select(k => new ApiKeyInfo
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
            })
            .ToListAsync();

        return Ok(new OASISResult<List<ApiKeyInfo>> { IsError = false, Message = "OK", Result = keys });
    }

    /// <summary>
    /// Revoke an API key (soft-delete — key is deactivated, not removed).
    /// </summary>
    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var avatarId = GetAvatarId();
        if (avatarId == Guid.Empty)
            return Unauthorized(new OASISResult<object> { IsError = true, Message = "Avatar not authenticated." });

        var key = await _db.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id && k.AvatarId == avatarId);

        if (key is null)
            return NotFound(new OASISResult<object> { IsError = true, Message = "API key not found." });

        key.IsActive = false;
        key.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new OASISResult<object> { IsError = false, Message = "API key revoked." });
    }

    /// <summary>
    /// Permanently delete an API key record.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var avatarId = GetAvatarId();
        if (avatarId == Guid.Empty)
            return Unauthorized(new OASISResult<object> { IsError = true, Message = "Avatar not authenticated." });

        var key = await _db.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id && k.AvatarId == avatarId);

        if (key is null)
            return NotFound(new OASISResult<object> { IsError = true, Message = "API key not found." });

        _db.ApiKeys.Remove(key);
        await _db.SaveChangesAsync();

        return Ok(new OASISResult<object> { IsError = false, Message = "API key deleted." });
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
