// SPDX-License-Identifier: UNLICENSED
//
// The federated session must BE the password session, not a look-alike.

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Auth;

namespace AZOA.WebAPI.Tests.Auth;

public class FederatedTokenParityTests
{
    private const string SigningKey = "federated-parity-test-signing-key-at-least-32-chars";
    private const string Password = "C0rrectHorse";

    /// <summary>
    /// Claim-for-claim parity between the token the password path issues and the token
    /// the federated path issues for the SAME avatar. Only the deliberately-unique
    /// claims (jti, auth_time) and the expiry may differ.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task Federated_and_password_tokens_carry_identical_claims()
    {
        var avatar = new AZOA.WebAPI.Models.Avatar
        {
            Id = Guid.NewGuid(),
            Username = "parity-user",
            Email = "parity@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            DappRole = AzoaDappRoles.User,
        };

        var configuration = BuildConfiguration();

        // Password path — through the real manager, exactly as AvatarController calls it.
        var store = new Mock<IAvatarStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<IAvatar>>.Success([avatar]));
        var manager = new AvatarManager(store.Object, configuration);

        var passwordLogin = await manager.LoginAsync(
            new AZOA.WebAPI.Models.AvatarLoginModel { Email = avatar.Email, Password = Password });
        passwordLogin.IsError.Should().BeFalse();

        // Federated path — the issuer the OAuth exchange endpoint uses.
        var federatedToken = new AvatarLoginTokenIssuer(configuration).IssueLoginToken(avatar);

        var passwordClaims = Claims(passwordLogin.Result!);
        var federatedClaims = Claims(federatedToken);

        // Same claim TYPES, in the same multiset.
        federatedClaims.Select(claim => claim.Type).Should()
            .BeEquivalentTo(passwordClaims.Select(claim => claim.Type));

        // Same VALUES for everything that identifies the subject and its authority.
        foreach (var type in new[]
                 {
                     JwtRegisteredClaimNames.Sub,
                     JwtRegisteredClaimNames.Email,
                     "unique_name",
                     AzoaClaims.TokenUse,
                     "dapp_role",
                 })
        {
            Values(federatedClaims, type).Should().BeEquivalentTo(
                Values(passwordClaims, type),
                $"the '{type}' claim must be identical across sign-in methods");
        }
    }

    /// <summary>
    /// A federated token must be a FULL-AUTHORITY login token. Inventing a distinct
    /// token_use would silently exclude every OAuth user from the FirstPartyLogin
    /// policy, and stamping act_as_tenant would make it look like a scoped tenant
    /// child credential.
    /// </summary>
    [Fact]
    public void A_federated_token_is_a_login_token_and_never_a_tenant_child_credential()
    {
        var avatar = new AZOA.WebAPI.Models.Avatar
        {
            Id = Guid.NewGuid(),
            Username = "oauth-user",
            Email = "oauth@example.com",
            PasswordHash = "unguessable",
            DappRole = AzoaDappRoles.User,
        };

        var claims = Claims(new AvatarLoginTokenIssuer(BuildConfiguration()).IssueLoginToken(avatar));

        Values(claims, AzoaClaims.TokenUse).Should().BeEquivalentTo([AzoaClaims.TokenUseLogin]);
        claims.Should().NotContain(claim => claim.Type == "act_as_tenant");
        Values(claims, JwtRegisteredClaimNames.Sub).Should().BeEquivalentTo([avatar.Id.ToString()]);
    }

    /// <summary>
    /// The dApp role and its implied scopes ride the federated token too. This is the
    /// concrete drift AvatarLoginTokenIssuer's remarks describe: the wallet path's
    /// hand-copied minter forgot exactly these claims.
    /// </summary>
    [Fact]
    public void A_federated_token_carries_the_dapp_role_scopes()
    {
        var developer = new AZOA.WebAPI.Models.Avatar
        {
            Id = Guid.NewGuid(),
            Username = "dev",
            Email = "dev@example.com",
            PasswordHash = "unguessable",
            DappRole = AzoaDappRoles.Developer,
        };

        var claims = Claims(new AvatarLoginTokenIssuer(BuildConfiguration()).IssueLoginToken(developer));

        Values(claims, "dapp_role").Should().BeEquivalentTo([AzoaDappRoles.Normalize(AzoaDappRoles.Developer)]);
        Values(claims, "scope").Should().Contain(AzoaScopes.DappDevelop);
    }

    /// <summary>Issuer, audience, and algorithm are the ones the JwtBearer handler validates.</summary>
    [Fact]
    public void A_federated_token_is_signed_for_the_same_issuer_audience_and_algorithm()
    {
        var avatar = new AZOA.WebAPI.Models.Avatar
        {
            Id = Guid.NewGuid(),
            Username = "u",
            Email = "u@example.com",
            PasswordHash = "unguessable",
        };

        var token = new JwtSecurityTokenHandler().ReadJwtToken(
            new AvatarLoginTokenIssuer(BuildConfiguration()).IssueLoginToken(avatar));

        token.Issuer.Should().Be("AZOA.WebAPI");
        token.Audiences.Should().BeEquivalentTo(["AZOA.Client"]);
        token.Header.Alg.Should().Be("HS256");
        token.ValidTo.Should().BeCloseTo(
            DateTime.UtcNow.Add(AvatarLoginTokenIssuer.TokenLifetime), TimeSpan.FromMinutes(2));
    }

    private static IConfiguration BuildConfiguration()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = SigningKey,
            ["Jwt:Issuer"] = "AZOA.WebAPI",
            ["Jwt:Audience"] = "AZOA.Client",
        }).Build();

    private static List<System.Security.Claims.Claim> Claims(string token)
        => new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.ToList();

    private static IEnumerable<string> Values(
        IEnumerable<System.Security.Claims.Claim> claims, string type)
        => claims.Where(claim => claim.Type == type).Select(claim => claim.Value);
}
