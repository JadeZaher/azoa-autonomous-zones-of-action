using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// Unit coverage for the tenant manager AFTER the user-self-sovereignty hard
/// cutover (2026-06-22). The load-bearing facts proven here:
///  • ProvisionChild mints a SELF-OWNED avatar (OwnerTenantId == null) — there is
///    no tenant-locked child any more (AC6).
///  • IssueChildCredential requires a LIVE ConsentGrant; with no grant it is
///    NotFound (404, never 403 — the isolation crux); the scope ceiling is
///    (tenant ∩ granted ∩ requested) (AC2/M2/M3); the token carries act_as_tenant
///    (C1/AC4); a revoked/expired grant denies issuance (AC5).
/// </summary>
public class TenantManagerTests
{
    private readonly Mock<IAvatarStore> _store;
    private readonly Mock<IConsentGrantStore> _grants;
    private readonly TenantManager _manager;

    public TenantManagerTests()
    {
        _store = new Mock<IAvatarStore>();
        _grants = new Mock<IConsentGrantStore>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "super-secret-key-for-testing-only-quite-long-enough!",
                ["Jwt:Issuer"] = "test",
                ["Jwt:Audience"] = "test"
            })
            .Build();

        // Default: no grants for any grantor (the consent-gated path denies).
        _grants.Setup(g => g.ListByGrantorAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<ConsentGrant>> { Result = Array.Empty<ConsentGrant>() });

        _manager = new TenantManager(_store.Object, config, _grants.Object);
    }

    private void GivenLiveGrant(Guid userId, Guid tenantId, params string[] scopes)
        => _grants.Setup(g => g.ListByGrantorAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<ConsentGrant>>
            {
                Result = new[]
                {
                    new ConsentGrant
                    {
                        Id = Guid.NewGuid(),
                        GrantorAvatarId = userId,
                        TenantId = tenantId,
                        Scopes = scopes.ToList(),
                        GrantedAt = DateTime.UtcNow.AddMinutes(-1),
                        ExpiresAt = null,
                        RevokedAt = null,
                    }
                }
            });

    // ── Hard cutover: provision mints SELF-OWNED (AC6) ────────────────────────

    [Fact]
    public async Task ProvisionChild_MintsSelfOwnedAvatar_NeverTenantLocked()
    {
        var tenantId = Guid.NewGuid();
        _store.Setup(s => s.GetByTenantAndExternalUserAsync(tenantId, "user-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = null });
        _store.Setup(s => s.UpsertAsync(It.IsAny<IAvatar>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAvatar a, CancellationToken _) => new AZOAResult<IAvatar> { Result = a });

        var result = await _manager.ProvisionChildAsync(tenantId, new ProvisionChildModel { ExternalUserId = "user-42" });

        result.IsError.Should().BeFalse();
        // The persisted avatar is SELF-OWNED — OwnerTenantId is null (no lock).
        _store.Verify(s => s.UpsertAsync(
            It.Is<IAvatar>(a => a.OwnerTenantId == null && a.ExternalUserId == "user-42"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProvisionChild_MissingExternalUserId_ReturnsError()
    {
        var result = await _manager.ProvisionChildAsync(Guid.NewGuid(), new ProvisionChildModel { ExternalUserId = "" });
        result.IsError.Should().BeTrue();
    }

    // ── Consent-gated issuance (AC2/M2) ───────────────────────────────────────

    [Fact]
    public async Task IssueChildCredential_NoGrant_ReturnsNotFound_NotForbidden()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = new Avatar { Id = userId, OwnerTenantId = null } });
        // No grant (default). Even though the avatar exists, issuance is denied.

        var result = await _manager.IssueChildCredentialAsync(
            tenantId, userId, Array.Empty<string>(), new[] { AzoaScopes.WalletManage });

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(TenantAuthorizationError.NotFound);
        result.Message.Should().NotStartWith(TenantAuthorizationError.Forbidden);
    }

    [Fact]
    public async Task IssueChildCredential_OwnershipAlone_IsNotEnough_M2()
    {
        // A legacy-style OwnerTenantId == tenant avatar with NO grant must STILL be
        // denied — there is no ownership-only issuance path after the cutover.
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = new Avatar { Id = userId, OwnerTenantId = tenantId } });

        var result = await _manager.IssueChildCredentialAsync(
            tenantId, userId, Array.Empty<string>(), new[] { AzoaScopes.WalletManage });

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(TenantAuthorizationError.NotFound);
    }

    [Fact]
    public async Task IssueChildCredential_WithLiveGrant_Succeeds_AndCarriesActAsTenant()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = new Avatar { Id = userId, OwnerTenantId = null } });
        GivenLiveGrant(userId, tenantId, AzoaScopes.WalletManage, AzoaScopes.NftMint);

        var result = await _manager.IssueChildCredentialAsync(
            tenantId, userId,
            requestedScopes: new[] { AzoaScopes.WalletManage },
            tenantScopes: new[] { AzoaScopes.TenantProvision, AzoaScopes.WalletManage, AzoaScopes.NftMint });

        result.IsError.Should().BeFalse();
        result.Result!.AvatarId.Should().Be(userId);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Result.Token);
        jwt.Subject.Should().Be(userId.ToString());
        // C1/AC4: the act_as_tenant claim marks the token as tenant-driven.
        jwt.Claims.Should().Contain(c => c.Type == TenantManager.ActAsTenantClaim && c.Value == tenantId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "scope" && c.Value == AzoaScopes.WalletManage);
    }

    [Fact]
    public async Task IssueChildCredential_ScopeCeiling_IsTenantIntersectGrantIntersectRequested_M3()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = new Avatar { Id = userId, OwnerTenantId = null } });
        // Grant covers wallet:manage ONLY (NOT nft:mint).
        GivenLiveGrant(userId, tenantId, AzoaScopes.WalletManage);

        var result = await _manager.IssueChildCredentialAsync(
            tenantId, userId,
            // Request both; tenant holds both — but the grant ceiling drops nft:mint.
            requestedScopes: new[] { AzoaScopes.WalletManage, AzoaScopes.NftMint },
            tenantScopes: new[] { AzoaScopes.WalletManage, AzoaScopes.NftMint });

        result.IsError.Should().BeFalse();
        result.Result!.Scopes.Should().BeEquivalentTo(new[] { AzoaScopes.WalletManage });
        result.Result.Scopes.Should().NotContain(AzoaScopes.NftMint);
    }

    [Fact]
    public async Task IssueChildCredential_RevokedGrant_Denied_AC5()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = new Avatar { Id = userId, OwnerTenantId = null } });
        _grants.Setup(g => g.ListByGrantorAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<ConsentGrant>>
            {
                Result = new[]
                {
                    new ConsentGrant
                    {
                        Id = Guid.NewGuid(), GrantorAvatarId = userId, TenantId = tenantId,
                        Scopes = new List<string> { AzoaScopes.WalletManage },
                        GrantedAt = DateTime.UtcNow.AddMinutes(-10),
                        RevokedAt = DateTime.UtcNow.AddMinutes(-1), // revoked
                    }
                }
            });

        var result = await _manager.IssueChildCredentialAsync(
            tenantId, userId, new[] { AzoaScopes.WalletManage }, new[] { AzoaScopes.WalletManage });

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(TenantAuthorizationError.NotFound);
    }

    [Fact]
    public async Task IssueChildCredential_CrossTenantGrant_DoesNotLeak()
    {
        // A grant exists, but to ANOTHER tenant. The asking tenant gets NotFound.
        var askingTenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = new Avatar { Id = userId, OwnerTenantId = null } });
        GivenLiveGrant(userId, otherTenant, AzoaScopes.WalletManage);

        var result = await _manager.IssueChildCredentialAsync(
            askingTenant, userId, new[] { AzoaScopes.WalletManage }, new[] { AzoaScopes.WalletManage });

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(TenantAuthorizationError.NotFound);
    }

    [Fact]
    public async Task IssueChildCredential_RespectsAuthNotBeforeWatermark_AC3b()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var watermark = DateTime.UtcNow.AddMinutes(30); // a future claim watermark
        _store.Setup(s => s.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar>
            {
                Result = new Avatar { Id = userId, OwnerTenantId = null, AuthNotBefore = watermark }
            });
        GivenLiveGrant(userId, tenantId, AzoaScopes.WalletManage);

        var result = await _manager.IssueChildCredentialAsync(
            tenantId, userId, new[] { AzoaScopes.WalletManage }, new[] { AzoaScopes.WalletManage });

        result.IsError.Should().BeFalse();
        // The issued token's nbf is at/after the watermark (cannot act before a claim).
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Result!.Token);
        jwt.ValidFrom.Should().BeOnOrAfter(watermark.AddSeconds(-2));
    }

    // ── List / Resolve scoping (unchanged isolation behaviour) ────────────────

    [Fact]
    public async Task ResolveChild_NoMatch_ReturnsNotFound()
    {
        var tenantId = Guid.NewGuid();
        _store.Setup(s => s.GetByTenantAndExternalUserAsync(tenantId, "ghost", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = null });

        var result = await _manager.ResolveChildAsync(tenantId, "ghost");

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(TenantAuthorizationError.NotFound);
    }
}

/// <summary>
/// Coverage for the single reusable scope-check helper used by the TenantScope
/// policy and the credential-issuer.
/// </summary>
public class ClaimsPrincipalScopeTests
{
    private static ClaimsPrincipal PrincipalWith(params string[] scopes)
    {
        var claims = scopes.Select(s => new Claim("scope", s));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public void HasScope_True_WhenScopeClaimPresent()
    {
        var p = PrincipalWith(AzoaScopes.TenantProvision, AzoaScopes.WalletManage);
        p.HasScope(AzoaScopes.TenantProvision).Should().BeTrue();
    }

    [Fact]
    public void HasScope_False_WhenAbsent()
    {
        var p = PrincipalWith(AzoaScopes.WalletManage);
        p.HasScope(AzoaScopes.TenantProvision).Should().BeFalse();
    }

    [Fact]
    public void GetActingTenantId_ReadsActAsTenantClaim()
    {
        var tenant = Guid.NewGuid();
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("act_as_tenant", tenant.ToString()) }, "Test"));
        p.GetActingTenantId().Should().Be(tenant);
    }

    [Fact]
    public void GetActingTenantId_NullForPlainPrincipal()
    {
        var p = PrincipalWith(AzoaScopes.WalletManage);
        p.GetActingTenantId().Should().BeNull();
    }

    [Fact]
    public void HasDappDevelopAccess_True_WhenRoleIsDeveloper()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("dapp_role", AzoaDappRoles.Developer) }, "Test"));
        p.HasDappDevelopAccess().Should().BeTrue();
    }

    [Fact]
    public void HasDappManageAccess_False_WhenRoleIsDeveloper()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("dapp_role", AzoaDappRoles.Developer) }, "Test"));
        p.HasDappManageAccess().Should().BeFalse();
    }

    [Fact]
    public void HasDappManageAccess_True_WhenRoleIsManager()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("dapp_role", AzoaDappRoles.Manager) }, "Test"));
        p.HasDappManageAccess().Should().BeTrue();
    }

    [Fact]
    public void HasDappDevelopAccess_False_WhenPlainUser()
    {
        var p = PrincipalWith(AzoaScopes.WalletManage);
        p.HasDappDevelopAccess().Should().BeFalse();
    }

    [Fact]
    public void CanSelfIssueApiKeyScope_DeniesDappScopesForPlainUser()
    {
        var p = PrincipalWith(AzoaScopes.WalletManage);
        p.CanSelfIssueApiKeyScope(AzoaScopes.DappDevelop).Should().BeFalse();
        p.CanSelfIssueApiKeyScope(AzoaScopes.DappManage).Should().BeFalse();
    }

    [Fact]
    public void CanSelfIssueApiKeyScope_DeniesStaleDappScopeWhenRoleWasDowngraded()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim("dapp_role", AzoaDappRoles.User),
                new Claim("scope", AzoaScopes.DappDevelop),
                new Claim("scope", AzoaScopes.DappManage),
            }, "Test"));

        p.CanSelfIssueApiKeyScope(AzoaScopes.DappDevelop).Should().BeFalse();
        p.CanSelfIssueApiKeyScope(AzoaScopes.DappManage).Should().BeFalse();
    }

    [Fact]
    public void CanSelfIssueApiKeyScope_AllowsDeveloperScopeOnlyForDeveloper()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("dapp_role", AzoaDappRoles.Developer) }, "Test"));
        p.CanSelfIssueApiKeyScope(AzoaScopes.DappDevelop).Should().BeTrue();
        p.CanSelfIssueApiKeyScope(AzoaScopes.DappManage).Should().BeFalse();
    }

    [Fact]
    public void CanSelfIssueApiKeyScope_AllowsBothDappScopesForManager()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("dapp_role", AzoaDappRoles.Manager) }, "Test"));
        p.CanSelfIssueApiKeyScope(AzoaScopes.DappDevelop).Should().BeTrue();
        p.CanSelfIssueApiKeyScope(AzoaScopes.DappManage).Should().BeTrue();
    }

    [Fact]
    public void CanSelfIssueApiKeyScope_NeverAllowsOperator()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim("dapp_role", AzoaDappRoles.Manager),
                new Claim("scope", AzoaScopes.Operator),
            }, "Test"));
        p.CanSelfIssueApiKeyScope(AzoaScopes.Operator).Should().BeFalse();
    }

    [Fact]
    public void CanSelfIssueApiKeyScope_NeverAllowsNodeGovern()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim("dapp_role", AzoaDappRoles.Manager),
                new Claim("scope", AzoaScopes.NodeGovern),
            }, "Test"));
        p.CanSelfIssueApiKeyScope(AzoaScopes.NodeGovern).Should().BeFalse();
    }
}
