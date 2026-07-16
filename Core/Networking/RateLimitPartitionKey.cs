using System.Security.Claims;

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
            if (string.Equals(authMethod, "ApiKey", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(apiKeyId))
            {
                return $"apikey:{apiKeyId}";
            }

            var subject = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value
                ?? user.FindFirst("AvatarId")?.Value;
            if (!string.IsNullOrWhiteSpace(subject))
                return $"avatar:{subject}";
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip:{ip}";
    }
}
