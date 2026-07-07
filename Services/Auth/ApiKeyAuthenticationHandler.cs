using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Services.Auth;

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
            var rawTokens = apiKey.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var emittedCount = 0;
            foreach (var scope2 in rawTokens)
            {
                // security-review HIGH-2 (defense-in-depth): never emit an admin-only
                // capability (e.g. operator:admin) as a scope claim from an API key,
                // even if a forged/misconfigured key CSV contains the literal string.
                // Operator authority must originate only from a real admin's JWT — the
                // Operator authorization policy additionally requires the JWT scheme.
                if (!AZOA.WebAPI.Core.AzoaScopes.IsApiKeyIssuableScope(scope2))
                    continue;
                claims.Add(new Claim("scope", scope2));
                emittedCount++;
            }

            // hardening review M3: a CSV that was non-empty (rawTokens.Length > 0) but
            // whose every token got dropped as forbidden must NOT be indistinguishable
            // from a genuinely-empty CSV — the latter is legacy "full access" in the
            // DappDevelop policy. Mark this case explicitly so the policy can deny it
            // instead of silently granting full access to an all-forbidden-scope key.
            if (rawTokens.Length > 0 && emittedCount == 0)
                claims.Add(new Claim("ScopesRestricted", "true"));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    public static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey));
        return AZOA.WebAPI.Helpers.Encoding.ToLowerHex(bytes);
    }

    public static string GenerateRawKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return $"azoa_{AZOA.WebAPI.Helpers.Encoding.ToLowerHex(bytes)}";
    }
}
