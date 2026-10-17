// SPDX-License-Identifier: UNLICENSED
//
// The wallet-authenticated session must carry the SAME authority as the password
// session for the identical user. WalletAuthManager.GenerateLoginJwt used to be a
// hand-copied mirror of UserManager.GenerateJwt that silently dropped the
// dapp_role/scope claims (never calling StampDappRoleClaims) — a wallet-authenticated
// user's token carried no dApp authority while a password login for the SAME user
// got both. Both paths now delegate to the one shared UserLoginTokenIssuer, so this
// suite asserts equivalence directly rather than the presence of specific claims: the
// next divergence fails regardless of which claim drifts.

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Auth;

public class WalletAuthTokenParityTests
{
    private const string SigningKey = "wallet-parity-test-signing-key-at-least-32-chars";
    private const string Password = "C0rrectHorse1!";
    private const string Addr = "ALGOADDRESSXYZ";
    private const string Chain = "algorand";

    private readonly Mock<IWalletAuthChallengeStore> _challenges = new();
    private readonly Mock<IWalletAuthClaimTokenStore> _claimTokens = new();
    private readonly Mock<IUserStore> _users = new();
    private readonly Mock<IWalletSignatureVerifier> _verifier = new();
    private readonly IConfiguration _config;
    private readonly WalletAuthManager _walletMgr;

    public WalletAuthTokenParityTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = SigningKey,
            ["Jwt:Issuer"] = "AZOA.WebAPI",
            ["Jwt:Audience"] = "AZOA.Client",
        }).Build();

        _walletMgr = new WalletAuthManager(
            _challenges.Object, _claimTokens.Object, _users.Object, _verifier.Object, _config);
    }

    /// <summary>
    /// Wires a live, verifiable challenge for <see cref="Addr"/>/<see cref="Chain"/> so
    /// <see cref="WalletAuthManager.VerifyAsync"/> reaches the login path.
    /// </summary>
    private void GivenLiveChallengeAndValidSignature()
    {
        const string nonce = "nonce-parity-1";
        var msg = $"AZOA-AUTH-v1\nissuer:AZOA.WebAPI\naudience:AZOA.Client\nchain:{Chain}\naddress:{Addr}\nnonce:{nonce}\nexpiry:2099-01-01T00:00:00Z";
        _challenges
            .Setup(c => c.GetLatestLiveByAddressAsync(Addr, Chain, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WalletAuthChallenge>
            {
                Result = new WalletAuthChallenge
                {
                    Address = Addr,
                    ChainType = Chain,
                    Nonce = nonce,
                    DomainMessage = msg,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(4),
                },
            });
        _challenges
            .Setup(c => c.TryConsumeAsync(nonce, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        _verifier.Setup(v => v.Verify(Chain, Addr, It.IsAny<byte[]>(), It.IsAny<byte[]>())).Returns(true);
    }

    /// <summary>Logs the given (already wallet-bound) user in through the wallet-verify path.</summary>
    private async Task<string> WalletLoginTokenAsync(IUser user)
    {
        GivenLiveChallengeAndValidSignature();
        _users
            .Setup(a => a.GetByAuthWalletAsync(Addr, Chain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser> { Result = user });

        var result = await _walletMgr.VerifyAsync(Addr, Chain, "c2ln", null);
        result.IsError.Should().BeFalse(result.Message);
        return result.Result!.Token;
    }

    /// <summary>Logs the identical user in through the password path (the reference token).</summary>
    private async Task<string> PasswordLoginTokenAsync(IUser user)
    {
        var store = new Mock<IUserStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<IUser>>.Success([user]));
        var passwordMgr = new UserManager(store.Object, _config);

        var result = await passwordMgr.LoginAsync(
            new UserLoginModel { Email = user.Email, Password = Password });
        result.IsError.Should().BeFalse(result.Message);
        return result.Result!;
    }

    private static List<Claim> Claims(string token)
        => new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.ToList();

    private static IEnumerable<string> Values(IEnumerable<Claim> claims, string type)
        => claims.Where(claim => claim.Type == type).Select(claim => claim.Value);

    /// <summary>
    /// The defect, asserted structurally: whatever claim TYPES a password login emits,
    /// a wallet login for the same user emits the same set — no claim is silently
    /// dropped on the wallet path. Only the deliberately-unique claims (jti, auth_time)
    /// and the expiry may differ in value.
    /// </summary>
    [Fact]
    public async Task Wallet_and_password_login_tokens_carry_equivalent_claim_sets()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "parity-wallet-user",
            Email = "parity-wallet@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            DappRole = AzoaDappRoles.Manager,
            AuthWalletAddress = Addr,
            AuthWalletChainType = Chain,
        };

        var passwordClaims = Claims(await PasswordLoginTokenAsync(user));
        var walletClaims = Claims(await WalletLoginTokenAsync(user));

        walletClaims.Select(c => c.Type).Should().BeEquivalentTo(passwordClaims.Select(c => c.Type));

        foreach (var type in new[]
                 {
                     JwtRegisteredClaimNames.Sub,
                     JwtRegisteredClaimNames.Email,
                     "unique_name",
                     AzoaClaims.TokenUse,
                     "dapp_role",
                 })
        {
            Values(walletClaims, type).Should().BeEquivalentTo(
                Values(passwordClaims, type),
                $"the '{type}' claim must be identical between wallet-auth and password logins for the same user");
        }

        // Multi-valued: the manager role implies BOTH dapp:develop and dapp:manage
        // scopes on either path — same multiset, not merely "non-empty".
        Values(walletClaims, "scope").Should().BeEquivalentTo(Values(passwordClaims, "scope"));
    }

    /// <summary>
    /// The concrete drift this fix closes: a wallet-authenticated user's token now
    /// carries its dApp role and the scopes that role implies, exactly like the
    /// UserLoginTokenIssuer remarks describe the password path always has.
    /// </summary>
    [Fact]
    public async Task A_wallet_login_token_carries_the_dapp_role_and_scope_claims()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "wallet-dev",
            Email = "wallet-dev@example.com",
            PasswordHash = "unguessable",
            DappRole = AzoaDappRoles.Developer,
            AuthWalletAddress = Addr,
            AuthWalletChainType = Chain,
        };

        var claims = Claims(await WalletLoginTokenAsync(user));

        Values(claims, "dapp_role").Should().BeEquivalentTo([AzoaDappRoles.Normalize(AzoaDappRoles.Developer)]);
        Values(claims, "scope").Should().Contain(AzoaScopes.DappDevelop);
    }

    /// <summary>
    /// End-to-end proof, not just at the token surface: a wallet-authenticated
    /// developer's token satisfies the exact assertion
    /// <c>Program.cs</c>'s <c>"DappDevelop"</c> policy runs for a non-API-key (JWT)
    /// principal. Before this fix, the same check on the OLD wallet token always
    /// evaluated to <c>false</c> — deny-by-default under-privilege, not a fail-open —
    /// because <see cref="ClaimsPrincipalExtensions.HasDappDeveloperRole"/> reads an
    /// absent <c>dapp_role</c> claim as the least-privileged <c>dapp:user</c> role.
    /// </summary>
    [Fact]
    public async Task A_wallet_authenticated_developer_satisfies_the_DappDevelop_policy_check()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "wallet-dev-2",
            Email = "wallet-dev-2@example.com",
            PasswordHash = "unguessable",
            DappRole = AzoaDappRoles.Developer,
            AuthWalletAddress = Addr,
            AuthWalletChainType = Chain,
        };

        var token = await WalletLoginTokenAsync(user);
        var principal = PrincipalFromToken(token);

        // No "AuthMethod" claim on a JWT principal (only ApiKeyAuthenticationHandler
        // stamps that) — this is exactly the non-API-key branch Program.cs's
        // "DappDevelop" / "DappManage" policies evaluate.
        principal.FindFirst("AuthMethod").Should().BeNull();
        principal.HasDappDevelopAccess().Should().BeTrue(
            "a wallet-authenticated developer must pass the same DappDevelop authorization check a password-authenticated developer passes");
    }

    /// <summary>Same end-to-end proof for the manager-only DappManage policy.</summary>
    [Fact]
    public async Task A_wallet_authenticated_manager_satisfies_the_DappManage_policy_check()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "wallet-manager",
            Email = "wallet-manager@example.com",
            PasswordHash = "unguessable",
            DappRole = AzoaDappRoles.Manager,
            AuthWalletAddress = Addr,
            AuthWalletChainType = Chain,
        };

        var token = await WalletLoginTokenAsync(user);
        var principal = PrincipalFromToken(token);

        principal.HasDappManageAccess().Should().BeTrue(
            "a wallet-authenticated manager must pass the same DappManage authorization check a password-authenticated manager passes");
    }

    private static ClaimsPrincipal PrincipalFromToken(string token)
        => new(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, "Test"));
}
