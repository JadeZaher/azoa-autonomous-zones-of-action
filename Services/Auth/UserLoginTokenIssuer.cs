// SPDX-License-Identifier: UNLICENSED

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>
/// The single place an interactive AZOA login JWT is minted.
/// </summary>
/// <remarks>
/// <para><b>Why this is shared and not copied.</b> Federated sign-in must issue the
/// SAME token the password path issues — same claim set, same lifetime, same signing
/// parameters — otherwise an OAuth session is a subtly different credential class and
/// every policy that reads <c>token_use</c>, <c>dapp_role</c>, or <c>auth_time</c> has
/// to be re-audited against a second shape. Extracting the mint makes that identity
/// structural: there is one implementation, so the two paths cannot drift.</para>
///
/// <para>The codebase already demonstrates the failure mode this prevents.
/// <c>WalletAuthManager.GenerateLoginJwt</c> is a hand-copied mirror of
/// <c>UserManager.GenerateJwt</c> and has silently fallen behind: it never calls
/// <c>StampDappRoleClaims</c>, so a wallet-authenticated user receives a token with
/// no <c>dapp_role</c> and no <c>scope</c> claims. Deliberately NOT changed here —
/// altering the wallet token's shape is outside this change's blast radius — but it is
/// exactly why a third copy was not written for OAuth.</para>
/// </remarks>
public interface IUserLoginTokenIssuer
{
    /// <summary>Mints a full-authority interactive login token for an user.</summary>
    string IssueLoginToken(IUser user);
}

/// <inheritdoc />
public sealed class UserLoginTokenIssuer : IUserLoginTokenIssuer
{
    /// <summary>Interactive login lifetime; identical for password and federated sign-in.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    private readonly IConfiguration _configuration;

    public UserLoginTokenIssuer(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <inheritdoc />
    public string IssueLoginToken(IUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return BuildLoginToken(_configuration, user);
    }

    /// <summary>
    /// Static so <c>UserManager</c> can call it without taking a constructor
    /// dependency, while both paths still run the one implementation.
    /// </summary>
    public static string BuildLoginToken(IConfiguration configuration, IUser user)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(user);

        var key = configuration.GetValue<string>("Jwt:Key")
            ?? throw new InvalidOperationException("JWT Key missing.");
        var securityKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Username),
            // token-type segregation (security-review S5): a login token and a tenant
            // child credential share key/issuer/audience/alg, so the explicit token_use
            // claim is what makes the credential class unambiguous. A federated login is
            // a full-authority interactive login and carries the SAME value — it is the
            // authentication *method* that differs, not the credential class, and
            // inventing a third token_use here would silently exclude OAuth users from
            // every FirstPartyLogin-gated endpoint.
            new Claim(AzoaClaims.TokenUse, AzoaClaims.TokenUseLogin),
            new Claim(
                AzoaClaims.AuthTime,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        StampDappRoleClaims(user, claims);

        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.Add(TokenLifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Adds the user's dApp role and the scopes that role implies.</summary>
    private static void StampDappRoleClaims(IUser user, List<Claim> claims)
    {
        var role = AzoaDappRoles.Normalize(user.DappRole);
        claims.Add(new Claim("dapp_role", role));

        if (AzoaDappRoles.CanDevelop(role))
            claims.Add(new Claim("scope", AzoaScopes.DappDevelop));

        if (AzoaDappRoles.CanManage(role))
            claims.Add(new Claim("scope", AzoaScopes.DappManage));
    }
}
