using System.Globalization;
using System.Security.Claims;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Helpers;

/// <summary>Reads timestamp evidence from an already authenticated AZOA JWT class.</summary>
public static class ValidatedTokenTime
{
    public static bool TryRead(ClaimsPrincipal? principal, out DateTimeOffset issuedAt, out DateTimeOffset expiresAt)
    {
        issuedAt = default;
        expiresAt = default;
        var kind = principal?.FindFirst(AzoaClaims.TokenUse)?.Value;
        var timestamp = kind switch
        {
            AzoaClaims.TokenUseLogin or AzoaClaims.TokenUseNodeOperator or NodeAdminClaims.TokenUseNodeAdmin => AzoaClaims.AuthTime,
            AzoaClaims.TokenUseChild => "nbf",
            _ => null,
        };
        if (timestamp is null || !ReadSingle(principal!, timestamp, out issuedAt) || !ReadSingle(principal!, "exp", out expiresAt)
            || issuedAt >= expiresAt) return false;
        if (principal!.HasClaim(claim => claim.Type == "iat")
            && (!ReadSingle(principal, "iat", out var minted) || minted > expiresAt || minted < issuedAt)) return false;
        return true;
    }

    private static bool ReadSingle(ClaimsPrincipal principal, string claim, out DateTimeOffset value)
    {
        value = default;
        var matches = principal.FindAll(claim).ToArray();
        if (matches.Length != 1 || !long.TryParse(matches[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 0 or > 253402300799) return false;
        value = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }
}
