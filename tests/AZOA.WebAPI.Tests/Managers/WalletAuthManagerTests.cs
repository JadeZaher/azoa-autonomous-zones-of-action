using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// user-sovereign-identity AC1/AC2/AC2b/AC3b: wallet-challenge verify (create-or-login
/// only, never takeover), atomic single-use nonce, and the claim watermark cut. The
/// stores + ed25519 verifier are mocked; the manager logic is under test.
/// </summary>
public class WalletAuthManagerTests
{
    private readonly Mock<IWalletAuthChallengeStore> _challenges = new();
    private readonly Mock<IWalletAuthClaimTokenStore> _claimTokens = new();
    private readonly Mock<IUserStore> _users = new();
    private readonly Mock<IWalletSignatureVerifier> _verifier = new();
    private readonly WalletAuthManager _mgr;

    private static readonly string Addr = new Algorand.Address(new byte[32]).ToString();
    private const string Chain = "algorand";
    private const string ClaimToken = "claim-token-abc";
    private static readonly string ClaimTokenHash = OpaqueTokenDigest.Compute(ClaimToken);
    private readonly Guid _tenant = Guid.NewGuid();

    public WalletAuthManagerTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "super-secret-key-for-testing-only-quite-long-enough!",
            ["Jwt:Issuer"] = "test",
            ["Jwt:Audience"] = "test",
        }).Build();
        _mgr = new WalletAuthManager(_challenges.Object, _claimTokens.Object, _users.Object, _verifier.Object, config);
    }

    private void GivenLiveChallengeAndValidSignature()
    {
        var nonce = "nonce-123";
        var expires = DateTime.UtcNow.AddMinutes(4);
        var msg = $"AZOA-AUTH-v1\nissuer:test\naudience:test\nchain:{Chain}\naddress:{Addr}\nnonce:{nonce}\nexpires:{expires:O}";
        _challenges.Setup(c => c.GetLatestLiveByAddressAsync(Addr, Chain, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WalletAuthChallenge>
            {
                Result = new WalletAuthChallenge
                {
                    Address = Addr, ChainType = Chain, Nonce = nonce, DomainMessage = msg,
                    ExpiresAt = expires,
                }
            });
        _challenges.Setup(c => c.TryConsumeAsync(nonce, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        _verifier.Setup(v => v.Verify(Chain, Addr, It.IsAny<byte[]>(), It.IsAny<byte[]>())).Returns(true);
    }

    [Fact]
    public async Task Verify_UnknownWallet_CreatesSelfOwnedUser_AC2()
    {
        GivenLiveChallengeAndValidSignature();
        // Wallet not bound to any user yet.
        _users.Setup(a => a.GetByAuthWalletAsync(Addr, Chain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser> { Result = null });
        _users.Setup(a => a.CreateIfAbsentAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IUser a, CancellationToken _) => new AZOAResult<IUser> { Result = a });

        var r = await _mgr.VerifyAsync(Addr, Chain, "c2ln", null);

        r.IsError.Should().BeFalse();
        r.Result!.Token.Should().NotBeNullOrEmpty();
        // The minted user is SELF-OWNED with the wallet binding set.
        _users.Verify(a => a.CreateIfAbsentAsync(
            It.Is<IUser>(x => x.OwnerTenantId == null
                && x.AuthWalletAddress == Addr && x.AuthWalletChainType == Chain),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Verify_KnownWallet_LogsIntoThatUser_NoNewUser_AC2()
    {
        GivenLiveChallengeAndValidSignature();
        var existing = new User { Id = Guid.NewGuid(), OwnerTenantId = null, AuthWalletAddress = Addr, AuthWalletChainType = Chain };
        _users.Setup(a => a.GetByAuthWalletAsync(Addr, Chain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser> { Result = existing });

        var r = await _mgr.VerifyAsync(Addr, Chain, "c2ln", null);

        r.IsError.Should().BeFalse();
        r.Result!.UserId.Should().Be(existing.Id);
        // No new user minted for a known wallet.
        _users.Verify(a => a.CreateIfAbsentAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Verify_BadSignature_Fails_AfterConsume()
    {
        GivenLiveChallengeAndValidSignature();
        _verifier.Setup(v => v.Verify(Chain, Addr, It.IsAny<byte[]>(), It.IsAny<byte[]>())).Returns(false);
        _users.Setup(a => a.GetByAuthWalletAsync(Addr, Chain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser> { Result = null });

        var r = await _mgr.VerifyAsync(Addr, Chain, "badsig", null);

        r.IsError.Should().BeTrue();
        // Never minted a user on a bad signature.
        _users.Verify(a => a.CreateIfAbsentAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Verify_NonceAlreadyConsumed_Fails_AC1()
    {
        var nonce = "nonce-123";
        var expires = DateTime.UtcNow.AddMinutes(4);
        var msg = $"AZOA-AUTH-v1\nissuer:test\naudience:test\nchain:{Chain}\naddress:{Addr}\nnonce:{nonce}\nexpires:{expires:O}";
        _challenges.Setup(c => c.GetLatestLiveByAddressAsync(Addr, Chain, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WalletAuthChallenge>
            {
                Result = new WalletAuthChallenge { Address = Addr, ChainType = Chain, Nonce = nonce, DomainMessage = msg, ExpiresAt = expires }
            });
        // Atomic consume LOSES (another concurrent verify already won).
        _challenges.Setup(c => c.TryConsumeAsync(nonce, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = false });

        var r = await _mgr.VerifyAsync(Addr, Chain, "c2ln", null);

        r.IsError.Should().BeTrue();
        // The signature verify never even runs once the consume is lost.
        _verifier.Verify(v => v.Verify(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task Verify_NoActiveChallenge_Fails()
    {
        _challenges.Setup(c => c.GetLatestLiveByAddressAsync(Addr, Chain, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WalletAuthChallenge> { Result = null });

        var r = await _mgr.VerifyAsync(Addr, Chain, "c2ln", null);

        r.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Verify_ClientMessageMismatch_Rejected_AC1b()
    {
        GivenLiveChallengeAndValidSignature();
        _users.Setup(a => a.GetByAuthWalletAsync(Addr, Chain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser> { Result = null });

        // A tampered client echo of the signed message must reject (domain separation).
        var r = await _mgr.VerifyAsync(Addr, Chain, "c2ln", message: "TAMPERED-MESSAGE");

        r.IsError.Should().BeTrue();
    }

    // ── AC3b: revoke-all-grants-on-claim (the residual-child-JWT cut) ──────────

    /// <summary>
    /// Wires the claim-token happy path: a single-use token resolves the target
    /// user, the user loads + echoes back on upsert. The user-side credential is
    /// a password (the simplest legal "exactly one of {password,wallet}" choice — it
    /// avoids the challenge/verify pipeline entirely so the test isolates the
    /// revoke-on-claim behaviour). Returns the claimed user id.
    /// </summary>
    private Guid GivenClaimTokenHappyPath()
    {
        var targetId = Guid.NewGuid();
        var tenant = _tenant;
        _claimTokens.Setup(t => t.GetByTokenHashAsync(ClaimTokenHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WalletAuthClaimToken>
            {
                Result = new WalletAuthClaimToken { TokenHash = ClaimTokenHash, TargetUserId = targetId, TenantId = tenant, ExpiresAt = DateTime.UtcNow.AddMinutes(5) }
            });
        // The tenant-provisioned user still has a tenant owner pre-claim.
        _users.Setup(a => a.GetByIdAsync(targetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser>
            {
                Result = new User { Id = targetId, OwnerTenantId = tenant }
            });
        _users.Setup(a => a.TryClaimAuthenticationAsync(It.IsAny<IUser>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<WalletAuthClaimToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IUser a, string? _, string? _, Guid _, DateTime? _, WalletAuthClaimToken _, CancellationToken _) => new AZOAResult<IUser> { Result = a });
        return targetId;
    }

    [Fact]
    public async Task Claim_atomic_authentication_failure_does_not_issue_a_login_token()
    {
        var id = GivenClaimTokenHappyPath();
        _users.Setup(store => store.TryClaimAuthenticationAsync(It.IsAny<IUser>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<WalletAuthClaimToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IUser>.Failure("Consent changed concurrently."));
        var result = await _mgr.ClaimAsync(id, "claim-token-abc", "a-strong-new-password", null, null, null, null);
        result.IsError.Should().BeTrue(); result.Result.Should().BeNull();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("identity")]
    [InlineData("credentials")]
    [InlineData("consumed")]
    public async Task Invalid_claim_never_enters_ownership_transaction(string condition)
    {
        var id = GivenClaimTokenHappyPath();
        if (condition == "consumed")
            _claimTokens.Setup(store => store.GetByTokenHashAsync(ClaimTokenHash, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<WalletAuthClaimToken>.Success(new WalletAuthClaimToken {
                    TargetUserId = id, TokenHash = ClaimTokenHash, TenantId = Guid.NewGuid(),
                    ConsumedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(5) }));
        var result = await _mgr.ClaimAsync(condition == "identity" ? Guid.NewGuid() : id,
            condition == "missing" ? null : "claim-token-abc", condition == "credentials" ? null : "new-password", null, null, null, null);
        result.IsError.Should().BeTrue(); result.Result.Should().BeNull();
        _users.Verify(store => store.TryClaimAuthenticationAsync(It.IsAny<IUser>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<WalletAuthClaimToken>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claim_revokes_both_tenant_known_credentials_in_the_authority_CAS(bool walletClaim)
    {
        var id = GivenClaimTokenHappyPath();
        var oldTenant = Guid.NewGuid();
        var oldWallet = new Algorand.Address(Enumerable.Repeat((byte)1, 32).ToArray()).ToString();
        var oldPassword = BCrypt.Net.BCrypt.HashPassword("tenant-known-password");
        var child = new User { Id = id, OwnerTenantId = oldTenant, PasswordHash = oldPassword, AuthWalletAddress = oldWallet, AuthWalletChainType = Chain };
        _users.Setup(store => store.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<IUser>.Success(child));
        _claimTokens.Setup(store => store.GetByTokenHashAsync(ClaimTokenHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<WalletAuthClaimToken>.Success(new WalletAuthClaimToken { TokenHash = ClaimTokenHash, TargetUserId = id, TenantId = oldTenant, ExpiresAt = DateTime.UtcNow.AddMinutes(5) }));
        if (walletClaim)
        {
            GivenLiveChallengeAndValidSignature();
            _users.Setup(store => store.GetByAuthWalletAsync(Addr, Chain, It.IsAny<CancellationToken>())).ReturnsAsync(new AZOAResult<IUser>());
        }
        var result = await _mgr.ClaimAsync(id, "claim-token-abc", walletClaim ? null : "new-user-password",
            walletClaim ? Addr : null, walletClaim ? Chain : null, walletClaim ? "c2ln" : null, null);
        result.IsError.Should().BeFalse();
        child.OwnerTenantId.Should().BeNull();
        child.AuthNotBefore.Should().NotBeNull();
        BCrypt.Net.BCrypt.Verify("tenant-known-password", child.PasswordHash).Should().BeFalse();
        child.AuthWalletAddress.Should().Be(walletClaim ? Addr : null);
        child.AuthWalletChainType.Should().Be(walletClaim ? Chain : null);
        _users.Verify(store => store.TryClaimAuthenticationAsync(child, oldWallet, Chain, oldTenant, null, It.IsAny<WalletAuthClaimToken>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>N1: default node config never hands the raw claim token to the minting tenant (account-takeover guard; see ClaimInviteOptionsTests for the Program.cs boot-guard counterpart).</summary>
    [Fact]
    public async Task CreateClaimInviteAsync_WithDefaultOptions_WithholdsTheRawTokenFromTheTenant()
    {
        var childUserId = Guid.NewGuid();
        _users.Setup(a => a.GetByIdAsync(childUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser> { Result = new User { Id = childUserId, OwnerTenantId = _tenant } });
        _claimTokens.Setup(t => t.CreateAsync(It.IsAny<WalletAuthClaimToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WalletAuthClaimToken token, CancellationToken _) => new AZOAResult<WalletAuthClaimToken> { Result = token });

        var result = await _mgr.CreateClaimInviteAsync(_tenant, childUserId);

        result.IsError.Should().BeFalse();
        result.Result!.ClaimToken.Should().BeNull(
            "a tenant holding the raw token could claim the member's account itself; only ReturnTokenToTenant=true (IntegrationTest/Development only) may echo it");
    }

    /// <summary>The manager's digest check is the authority, not the lookup: a pre-hashing row holding the raw token never authorizes, however it was found.</summary>
    [Fact]
    public async Task Claim_refuses_a_legacy_plaintext_invite_even_when_a_lookup_surfaces_it()
    {
        var id = GivenClaimTokenHappyPath();
        _claimTokens.Setup(store => store.GetByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<WalletAuthClaimToken>.Success(new WalletAuthClaimToken
            {
                TokenHash = ClaimToken, TargetUserId = id, TenantId = _tenant, ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            }));

        var result = await _mgr.ClaimAsync(id, ClaimToken, "member-chosen-password", null, null, null, null);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Invalid or expired claim token");
        _users.Verify(store => store.TryClaimAuthenticationAsync(It.IsAny<IUser>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<WalletAuthClaimToken>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
