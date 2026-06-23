using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// security-review S5: a login token and a tenant child credential are signed with the
/// SAME Jwt:Key / HmacSha256 / issuer / audience. Distinguishing them ONLY by the
/// presence of <c>act_as_tenant</c> is fragile, so an explicit <c>token_use</c> claim
/// marks the credential class unambiguously. These tests drive both real generators
/// through their public surface, decode the minted JWTs, and prove the markers:
/// <list type="bullet">
///   <item>LOGIN token (WalletAuthManager) → <c>token_use=login</c>, NO act_as_tenant.</item>
///   <item>CHILD token (TenantManager) → <c>token_use=child</c> AND act_as_tenant present.</item>
/// </list>
/// </summary>
public class TokenUseClaimTests
{
    private static IConfiguration TestConfig()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "super-secret-key-for-testing-only-quite-long-enough!",
            ["Jwt:Issuer"] = "test",
            ["Jwt:Audience"] = "test",
        }).Build();

    // ── LOGIN token (WalletAuthManager.VerifyAsync on an unknown wallet) ──────

    [Fact]
    public async Task LoginToken_CarriesTokenUseLogin_AndNoActAsTenant()
    {
        const string addr = "ALGOADDRESSXYZ";
        const string chain = "algorand";

        var challenges = new Mock<IWalletAuthChallengeStore>();
        var claimTokens = new Mock<IWalletAuthClaimTokenStore>();
        var avatars = new Mock<IAvatarStore>();
        var verifier = new Mock<IWalletSignatureVerifier>();
        var consentGrants = new Mock<IConsentGrantStore>();

        // Live challenge + winning consume + valid signature (mirror Verify_UnknownWallet).
        var nonce = "nonce-123";
        var msg = $"AZOA-AUTH-v1\nissuer:test\naudience:test\nchain:{chain}\naddress:{addr}\nnonce:{nonce}\nexpiry:2099-01-01T00:00:00Z\n";
        challenges.Setup(c => c.GetLatestLiveByAddressAsync(addr, chain, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WalletAuthChallenge>
            {
                Result = new WalletAuthChallenge
                {
                    Address = addr, ChainType = chain, Nonce = nonce, DomainMessage = msg,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(4),
                }
            });
        challenges.Setup(c => c.TryConsumeAsync(nonce, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        verifier.Setup(v => v.Verify(chain, addr, It.IsAny<byte[]>(), It.IsAny<byte[]>())).Returns(true);

        // Unknown wallet → mint a self-owned avatar and return a LOGIN token.
        avatars.Setup(a => a.GetByAuthWalletAsync(addr, chain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = null });
        avatars.Setup(a => a.UpsertAsync(It.IsAny<IAvatar>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAvatar a, CancellationToken _) => new AZOAResult<IAvatar> { Result = a });

        var mgr = new WalletAuthManager(
            challenges.Object, claimTokens.Object, avatars.Object, verifier.Object,
            consentGrants.Object, TestConfig());

        var r = await mgr.VerifyAsync(addr, chain, "c2ln", null);

        r.IsError.Should().BeFalse();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(r.Result!.Token);
        // Property: a login token is unambiguously a full-authority user login.
        jwt.Claims.Should().Contain(c => c.Type == AzoaClaims.TokenUse && c.Value == AzoaClaims.TokenUseLogin);
        // And can NEVER be mistaken for tenant-driven — no act_as_tenant claim.
        jwt.Claims.Should().NotContain(c => c.Type == TenantManager.ActAsTenantClaim);
    }

    // ── CHILD token (TenantManager.IssueChildCredentialAsync) ────────────────

    [Fact]
    public async Task ChildToken_CarriesTokenUseChild_AndActAsTenant()
    {
        var store = new Mock<IAvatarStore>();
        var grants = new Mock<IConsentGrantStore>();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        store.Setup(s => s.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = new Avatar { Id = userId, OwnerTenantId = null } });

        // A LIVE consent grant from this user to this tenant (mirror TenantManagerTests).
        grants.Setup(g => g.ListByGrantorAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<ConsentGrant>>
            {
                Result = new[]
                {
                    new ConsentGrant
                    {
                        Id = Guid.NewGuid(),
                        GrantorAvatarId = userId,
                        TenantId = tenantId,
                        Scopes = new List<string> { AzoaScopes.WalletManage },
                        GrantedAt = DateTime.UtcNow.AddMinutes(-1),
                        ExpiresAt = null,
                        RevokedAt = null,
                    }
                }
            });

        var manager = new TenantManager(store.Object, TestConfig(), grants.Object);

        var result = await manager.IssueChildCredentialAsync(
            tenantId, userId,
            requestedScopes: new[] { AzoaScopes.WalletManage },
            tenantScopes: new[] { AzoaScopes.WalletManage });

        result.IsError.Should().BeFalse();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Result!.Token);
        // The child credential is unambiguously scoped/tenant-driven.
        jwt.Claims.Should().Contain(c => c.Type == AzoaClaims.TokenUse && c.Value == AzoaClaims.TokenUseChild);
        jwt.Claims.Should().Contain(c => c.Type == TenantManager.ActAsTenantClaim && c.Value == tenantId.ToString());
    }
}
