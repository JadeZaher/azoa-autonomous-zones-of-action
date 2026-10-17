using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Services.Admin;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "FirstPartyLogin")]
public class ApiKeyController : ControllerBase
{
    private readonly IApiKeyStore _store;
    private readonly IUserStore _users;

    public ApiKeyController(IApiKeyStore store, IUserStore users)
    {
        _store = store;
        _users = users;
    }

    /// <summary>Operator-only issuance of one fixed-scope tenant integration key.</summary>
    [HttpPost("tenant")]
    [Authorize(Policy = "Operator")]
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [EnableRateLimiting("financial")]
    public async Task<IActionResult> CreateTenantKey([FromBody] CreateTenantApiKeyRequest request)
    {
        if (request.TenantUserId == Guid.Empty)
            return BadRequest(AZOAResult<CreateApiKeyResponse>.Failure("TenantUserId is required."));
        if (request.TenantUserId == NodeOperatorIdentity.UserId)
            return BadRequest(AZOAResult<CreateApiKeyResponse>.Failure(
                "The reserved node operator identity cannot own API keys."));
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
            return BadRequest(AZOAResult<CreateApiKeyResponse>.Failure(
                "Name must be a non-empty value of at most 100 characters."));

        var expiresInDays = request.ExpiresInDays ?? 90;
        if (expiresInDays is < 1 or > 365)
            return BadRequest(AZOAResult<CreateApiKeyResponse>.Failure(
                "ExpiresInDays must be between 1 and 365."));

        var tenant = await _users.GetByIdAsync(
            request.TenantUserId,
            HttpContext.RequestAborted);
        if (tenant.Message.StartsWith("USER_STORE_UNAVAILABLE:", StringComparison.Ordinal))
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                AZOAResult<CreateApiKeyResponse>.Failure("Tenant user could not be verified."));
        if (tenant.IsError || tenant.Result is null)
            return NotFound(AZOAResult<CreateApiKeyResponse>.Failure("Tenant user not found."));

        const string tenantScopes =
            $"{AzoaScopes.TenantProvision},{AzoaScopes.WalletManage},{AzoaScopes.KycRead},{AzoaScopes.KycSubmit}";
        var rawKey = ApiKeyAuthenticationHandler.GenerateRawKey();
        var apiKey = new ApiKey
        {
            UserId = request.TenantUserId,
            Name = request.Name.Trim(),
            KeyHash = ApiKeyAuthenticationHandler.HashKey(rawKey),
            KeyPrefix = rawKey[..16],
            ExpiresAt = DateTime.UtcNow.AddDays(expiresInDays),
            Scopes = tenantScopes,
            AllowedOrigins = null
        };

        await _store.CreateAsync(apiKey, HttpContext.RequestAborted);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        return Ok(AZOAResult<CreateApiKeyResponse>.Success(new CreateApiKeyResponse
        {
            Id = apiKey.Id,
            Name = apiKey.Name,
            Key = rawKey,
            KeyPrefix = apiKey.KeyPrefix,
            ExpiresAt = apiKey.ExpiresAt,
            Scopes = apiKey.Scopes,
            AllowedOrigins = null,
            CreatedDate = apiKey.CreatedDate
        }, "Tenant API key created. Store it securely; it will not be shown again."));
    }

    private Guid GetUserId()
    {
        var claim = User.FindFirst("UserId")?.Value
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
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new AZOAResult<object> { IsError = true, Message = "User not authenticated." });

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
            return BadRequest(AZOAResult<object>.Failure(
                "Name must be a non-empty value of at most 100 characters."));
        if (request.ExpiresInDays is < 1 or > 365)
            return BadRequest(AZOAResult<object>.Failure(
                "ExpiresInDays must be between 1 and 365."));
        if (string.IsNullOrWhiteSpace(request.Scopes))
            return BadRequest(AZOAResult<object>.Failure(
                "Select at least one explicit API-key scope."));

        var requestedScopes = request.Scopes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requestedScopes.Count == 0)
            return BadRequest(AZOAResult<object>.Failure(
                "Select at least one explicit API-key scope."));

        var rejected = requestedScopes
            .Where(s => !User.CanSelfIssueApiKeyScope(s))
            .ToList();
        if (rejected.Count > 0)
        {
            return BadRequest(new AZOAResult<object>
            {
                IsError = true,
                Message = $"The following scope(s) may not be issued on your own key: {string.Join(", ", rejected)}.",
            });
        }

        var rawKey = ApiKeyAuthenticationHandler.GenerateRawKey();
        var keyHash = ApiKeyAuthenticationHandler.HashKey(rawKey);

        var apiKey = new ApiKey
        {
            UserId = userId,
            Name = request.Name.Trim(),
            KeyHash = keyHash,
            KeyPrefix = rawKey[..16],
            ExpiresAt = request.ExpiresInDays.HasValue
                ? DateTime.UtcNow.AddDays(request.ExpiresInDays.Value)
                : null,
            Scopes = string.Join(',', requestedScopes),
            AllowedOrigins = request.AllowedOrigins,
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
                AllowedOrigins = apiKey.AllowedOrigins,
                CreatedDate = apiKey.CreatedDate,
            }
        });
    }

    /// <summary>
    /// List the scopes a user may self-issue on a new key, each with a human
    /// description. Drives the key-creation UI's scope checkboxes. Any authenticated
    /// user may read this — it exposes only the public scope vocabulary.
    /// </summary>
    [HttpGet("scopes")]
    public IActionResult IssuableScopes()
    {
        var scopes = AzoaScopes.IssuableScopeCatalog()
            .Where(s => User.CanSelfIssueApiKeyScope(s.Scope))
            .Select(s => new ApiKeyScopeInfo
            {
                Scope = s.Scope,
                Description = s.Description,
                IsSelfIssuable = true,
            })
            .ToList();

        return Ok(new AZOAResult<List<ApiKeyScopeInfo>> { IsError = false, Message = "OK", Result = scopes });
    }

    /// <summary>
    /// List all API keys for the authenticated user (keys are never shown, only prefixes).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new AZOAResult<object> { IsError = true, Message = "User not authenticated." });

        var owned = await _store.ListByUserAsync(userId, HttpContext.RequestAborted);

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
            AllowedOrigins = k.AllowedOrigins,
        }).ToList();

        return Ok(new AZOAResult<List<ApiKeyInfo>> { IsError = false, Message = "OK", Result = keys });
    }

    /// <summary>
    /// Revoke an API key (soft-delete — key is deactivated, not removed).
    /// </summary>
    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new AZOAResult<object> { IsError = true, Message = "User not authenticated." });

        var ok = await _store.RevokeAsync(id, userId, DateTime.UtcNow, HttpContext.RequestAborted);
        if (!ok && await _store.GetByIdForUserAsync(id, userId, HttpContext.RequestAborted) is not { IsActive: false })
            return NotFound(new AZOAResult<object> { IsError = true, Message = "API key not found." });

        return Ok(new AZOAResult<object> { IsError = false, Message = "API key revoked." });
    }

    /// <summary>
    /// Rotate an ACTIVE, explicitly-scoped key whose scopes the caller may still self-issue: revoke it,
    /// mint a successor with the same name, scopes and remaining expiry, and return the raw key once.
    /// </summary>
    [HttpPost("{id:guid}/rotate")]
    public async Task<IActionResult> Rotate(Guid id)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new AZOAResult<object> { IsError = true, Message = "User not authenticated." });

        var existing = await _store.GetByIdForUserAsync(id, userId, HttpContext.RequestAborted);
        if (existing is null || !existing.IsActive || existing.RevokedAt is not null
            || existing.ExpiresAt is { } expiry && expiry <= DateTime.UtcNow)
            return NotFound(new AZOAResult<object> { IsError = true, Message = "API key not found." });
        var inherited = (existing.Scopes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        if (inherited.Count == 0)
            return BadRequest(AZOAResult<object>.Failure("A legacy full-access key cannot be rotated; create a key with explicit scopes."));
        var notIssuable = inherited.Where(scope => !User.CanSelfIssueApiKeyScope(scope)).ToList();
        if (notIssuable.Count > 0)
            return BadRequest(AZOAResult<object>.Failure(
                $"The key carries scope(s) you may not issue on your own key: {string.Join(", ", notIssuable)}."));
        // Revoke first: the active-key CAS makes a concurrent second rotate lose instead of minting two successors.
        if (!await _store.RevokeAsync(id, userId, DateTime.UtcNow, HttpContext.RequestAborted))
            return NotFound(new AZOAResult<object> { IsError = true, Message = "API key not found." });

        var rawKey = ApiKeyAuthenticationHandler.GenerateRawKey();
        var keyHash = ApiKeyAuthenticationHandler.HashKey(rawKey);

        // Inherit the remaining expiry window (relative to now), not the original
        // absolute instant, so a rotated key isn't born already-expired.
        DateTime? expiresAt = existing.ExpiresAt.HasValue
            ? (existing.ExpiresAt.Value > DateTime.UtcNow ? existing.ExpiresAt.Value : DateTime.UtcNow)
            : null;

        var apiKey = new ApiKey
        {
            UserId = userId,
            Name = existing.Name,
            KeyHash = keyHash,
            KeyPrefix = rawKey[..16],
            ExpiresAt = expiresAt,
            Scopes = string.Join(',', inherited),
            AllowedOrigins = existing.AllowedOrigins,
        };

        await _store.CreateAsync(apiKey, HttpContext.RequestAborted);

        return Ok(new AZOAResult<CreateApiKeyResponse>
        {
            IsError = false,
            Message = "API key rotated. Store the new key securely — it will not be shown again. The old key is revoked.",
            Result = new CreateApiKeyResponse
            {
                Id = apiKey.Id,
                Name = apiKey.Name,
                Key = rawKey,
                KeyPrefix = apiKey.KeyPrefix,
                ExpiresAt = apiKey.ExpiresAt,
                Scopes = apiKey.Scopes,
                AllowedOrigins = apiKey.AllowedOrigins,
                CreatedDate = apiKey.CreatedDate,
            }
        });
    }

    /// <summary>
    /// Permanently delete an API key record.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new AZOAResult<object> { IsError = true, Message = "User not authenticated." });

        var ok = await _store.DeleteAsync(id, userId, HttpContext.RequestAborted);
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
    public string? AllowedOrigins { get; set; }
}

/// <summary>Operator request that binds a fixed-scope key to an existing user.</summary>
public sealed class CreateTenantApiKeyRequest
{
    public Guid TenantUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? ExpiresInDays { get; set; }
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
    public string? AllowedOrigins { get; set; }
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
    public string? AllowedOrigins { get; set; }
}

/// <summary>One self-issuable scope with its human description (key-issuance discovery surface).</summary>
public class ApiKeyScopeInfo
{
    public string Scope { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsSelfIssuable { get; set; }
}
