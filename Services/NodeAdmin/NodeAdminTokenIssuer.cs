using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AZOA.WebAPI.Core;
using Microsoft.IdentityModel.Tokens;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Revision watermarks a token is bound to: (admin_revision, session_revision) or (root_key_revision, root_session_revision).</summary>
public sealed record NodeAdminTokenRevisions(string FirstClaim, long First, string SecondClaim, long Second);

/// <summary>Mints HS256 node-admin session tokens on the shared <c>Jwt:Key</c> (asymmetric signing deferred, plan §7).</summary>
public sealed class NodeAdminTokenIssuer(IConfiguration configuration)
{
    public string Issue(string principal, string sid, DateTimeOffset authTime, IReadOnlyList<string> amr,
        NodeAdminTokenRevisions revisions, DateTimeOffset expiresAt)
    {
        var key = configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key missing.");
        var claims = new List<Claim>
        {
            new(NodeAdminClaims.Subject, principal),
            new(AzoaClaims.TokenUse, NodeAdminClaims.TokenUseNodeAdmin),
            new(NodeAdminClaims.SessionId, sid),
            new(AzoaClaims.AuthTime, authTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
            new(revisions.FirstClaim, revisions.First.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new(revisions.SecondClaim, revisions.Second.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        };
        claims.AddRange(amr.Select(method => new Claim(NodeAdminClaims.AuthMethods, method)));
        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims: claims,
            notBefore: authTime.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
