using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace AZOA.WebAPI.Core;

/// <summary>Claim vocabulary of node-admin session tokens (no permission claims: permissions are computed live, C2).</summary>
public static class NodeAdminClaims
{
    public const string TokenUseNodeAdmin = "node_admin";
    public const string Subject = "sub";
    public const string SessionId = "sid";
    public const string AuthMethods = "amr";
    public const string AdminRevision = "admin_revision";
    public const string SessionRevision = "session_revision";
    public const string RootKeyRevision = "root_key_revision";
    public const string RootSessionRevision = "root_session_revision";
    public const string AdminPrincipalPrefix = "admin:";
    public const string RootPrincipalPrefix = "root:";

    public static bool IsNodeAdmin(ClaimsPrincipal? principal)
        => string.Equals(principal?.FindFirst(AzoaClaims.TokenUse)?.Value, TokenUseNodeAdmin, StringComparison.Ordinal);

    /// <summary>Reads a claim by its JWT name or the inbound-mapped CLR name.</summary>
    public static string? Find(ClaimsPrincipal? principal, string jwtName)
    {
        if (principal is null) return null;
        var direct = principal.FindFirst(jwtName)?.Value;
        if (direct is not null) return direct;
        return JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.TryGetValue(jwtName, out var mapped)
            ? principal.FindFirst(mapped)?.Value
            : null;
    }

    public static string AdminPrincipal(Guid adminId) => AdminPrincipalPrefix + adminId.ToString("D");

    public static string RootPrincipal(string fingerprint) => RootPrincipalPrefix + fingerprint;
}
