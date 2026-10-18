using System.Net;
using System.Net.Sockets;
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
                ?? user.FindFirst("UserId")?.Value;
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
                return $"user:{subject}";
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip:{ip}";
    }

    /// <summary>Client-address partition: IPv4 (incl. IPv4-mapped) by full address, IPv6 by its /64 subscriber prefix.</summary>
    public static string Address(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    public static string ResolveTenantSubject(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tenant = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst("UserId")?.Value;
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
