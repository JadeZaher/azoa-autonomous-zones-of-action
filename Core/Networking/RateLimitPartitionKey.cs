using System.Security.Claims;
using System.Security.Cryptography;

namespace AZOA.WebAPI.Core.Networking;

public static class RateLimitPartitionKey
{
    public static string Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var authMethod = user.FindFirst("AuthMethod")?.Value;
            var apiKeyId = user.FindFirst("ApiKeyId")?.Value;
            var subject = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value
                ?? user.FindFirst("AvatarId")?.Value;
            if (string.Equals(authMethod, "ApiKey", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(apiKeyId))
            {
                var tenantScoped = user.FindAll("scope")
                    .SelectMany(claim => claim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    .Any(scope => string.Equals(
                        scope.Trim(),
                        AZOA.WebAPI.Core.AzoaScopes.TenantProvision,
                        StringComparison.Ordinal));
                if (tenantScoped && !string.IsNullOrWhiteSpace(subject))
                    return $"tenant:{subject}";

                return $"apikey:{apiKeyId}";
            }

            if (!string.IsNullOrWhiteSpace(subject))
                return $"avatar:{subject}";
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip:{ip}";
    }

    public static string ResolveTenantSubject(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tenant = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst("AvatarId")?.Value;
        var externalSubject = context.Request.RouteValues["externalSubject"]?.ToString();
        if (context.User.Identity?.IsAuthenticated != true
            || string.IsNullOrWhiteSpace(tenant)
            || string.IsNullOrWhiteSpace(externalSubject))
        {
            return $"tenant-subject:{Resolve(context)}:unknown";
        }

        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(externalSubject.Trim()));
        return $"tenant-subject:{tenant}:{Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant()}";
    }
}
