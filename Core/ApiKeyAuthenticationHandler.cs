using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Core;

public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    private const string HeaderName = "X-Api-Key";

    private readonly IServiceScopeFactory _scopeFactory;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IServiceScopeFactory scopeFactory)
        : base(options, logger, encoder)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var apiKeyValues))
        {
            return AuthenticateResult.NoResult();
        }

        var rawKey = apiKeyValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return AuthenticateResult.Fail("API key is empty.");
        }

        var keyHash = HashKey(rawKey);

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IApiKeyStore>();

        var apiKey = await store.GetByHashAsync(keyHash, Context.RequestAborted);
        if (apiKey is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        if (!apiKey.IsActive || apiKey.RevokedAt.HasValue)
        {
            return AuthenticateResult.Fail("API key has been revoked.");
        }

        if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt.Value < DateTime.UtcNow)
        {
            return AuthenticateResult.Fail("API key has expired.");
        }

        // Update last_used timestamp on a detached scope so a slow / failing
        // DB write never blocks (or fails) the request being authenticated.
        // TouchLastUsedAsync contract: must not throw — see IApiKeyStore.
        _ = Task.Run(async () =>
        {
            using var updateScope = _scopeFactory.CreateScope();
            var updateStore = updateScope.ServiceProvider.GetRequiredService<IApiKeyStore>();
            await updateStore.TouchLastUsedAsync(apiKey.Id, DateTime.UtcNow, CancellationToken.None);
        });

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, apiKey.AvatarId.ToString()),
            new("sub", apiKey.AvatarId.ToString()),
            new("AvatarId", apiKey.AvatarId.ToString()),
            new("ApiKeyId", apiKey.Id.ToString()),
            new("AuthMethod", "ApiKey"),
        };

        if (!string.IsNullOrEmpty(apiKey.Scopes))
        {
            foreach (var scope2 in apiKey.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim("scope", scope2));
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    public static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string GenerateRawKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return $"azoa_{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }
}
