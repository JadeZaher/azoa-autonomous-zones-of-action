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
    private readonly Mock<IAvatarStore> _avatars = new();
    private readonly Mock<IWalletSignatureVerifier> _verifier = new();
    private readonly Mock<IConsentGrantStore> _consentGrants = new();
    private readonly WalletAuthManager _mgr;

    private const string Addr = "ALGOADDRESSXYZ";
    private const string Chain = "algorand";

    public WalletAuthManagerTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "super-secret-key-for-testing-only-quite-long-enough!",
            ["Jwt:Issuer"] = "test",
            ["Jwt:Audience"] = "test",
        }).Build();
        // Default: revoke-all-on-claim succeeds (AC3b). Individual tests override to
        // assert the call happens / fails gracefully.
        _consentGrants.Setup(c => c.RevokeAllByGrantorAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<int> { Result = 0 });
        _mgr = new WalletAuthManager(_challenges.Object, _claimTokens.Object, _avatars.Object, _verifier.Object, _consentGrants.Object, config);
    }

    private void GivenLiveChallengeAndValidSignature()
    {
        var nonce = "nonce-123";
        var msg = $"AZOA-AUTH-v1\nissuer:test\naudience:test\nchain:{Chain}\naddress:{Addr}\nnonce:{nonce}\nexpiry:2099-01-01T00:00:00Z\n";
        _challenges.Setup(c => c.GetLatestLiveByAddressAsync(Addr, Chain, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WalletAuthChallenge>
            {
                Result = new WalletAuthChallenge
                {
                    Address = Addr, ChainType = Chain, Nonce = nonce, DomainMessage = msg,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(4),
                }
            });
        _challenges.Setup(c => c.TryConsumeAsync(nonce, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        _verifier.Setup(v => v.Verify(Chain, Addr, It.IsAny<byte[]>(), It.IsAny<byte[]>())).Returns(true);
    }

    [Fact]
    public async Task Verify_UnknownWallet_CreatesSelfOwnedAvatar_AC2()
    {
        GivenLiveChallengeAndValidSignature();
        // Wallet not bound to any avatar yet.
        _avatars.Setup(a => a.GetByAuthWalletAsync(Addr, Chain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = null });
        _avatars.Setup(a => a.UpsertAsync(It.IsAny<IAvatar>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAvatar a, CancellationToken _) => new AZOAResult<IAvatar> { Result = a });

        var r = await _mgr.VerifyAsync(Addr, Chain, "c2ln", null);

        r.IsError.Should().BeFalse();
        r.Result!.Token.Should().NotBeNullOrEmpty();
        // The minted avatar is SELF-OWNED with the wallet binding set.
        _avatars.Verify(a => a.UpsertAsync(
            It.Is<IAvatar>(x => x.OwnerTenantId == null
                && x.AuthWalletAddress == Addr && x.AuthWalletChainType == Chain),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Verify_KnownWallet_LogsIntoThatAvatar_NoNewAvatar_AC2()
    {
        GivenLiveChallengeAndValidSignature();
        var existing = new Avatar { Id = Guid.NewGuid(), OwnerTenantId = null, AuthWalletAddress = Addr, AuthWalletChainType = Chain };
        _avatars.Setup(a => a.GetByAuthWalletAsync(Addr, Chain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = existing });

        var r = await _mgr.VerifyAsync(Addr, Chain, "c2ln", null);

        r.IsError.Should().BeFalse();
        r.Result!.AvatarId.Should().Be(existing.Id);
        // No new avatar minted for a known wallet.
        _avatars.Verify(a => a.UpsertAsync(It.IsAny<IAvatar>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Verify_BadSignature_Fails_AfterConsume()
    {
        GivenLiveChallengeAndValidSignature();
        _verifier.Setup(v => v.Verify(Chain, Addr, It.IsAny<byte[]>(), It.IsAny<byte[]>())).Returns(false);
        _avatars.Setup(a => a.GetByAuthWalletAsync(Addr, Chain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = null });

        var r = await _mgr.VerifyAsync(Addr, Chain, "badsig", null);

        r.IsError.Should().BeTrue();
        // Never minted an avatar on a bad signature.
        _avatars.Verify(a => a.UpsertAsync(It.IsAny<IAvatar>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Verify_NonceAlreadyConsumed_Fails_AC1()
    {
        var nonce = "nonce-123";
        var msg = $"AZOA-AUTH-v1\nissuer:test\naudience:test\nchain:{Chain}\naddress:{Addr}\nnonce:{nonce}\nexpiry:2099-01-01T00:00:00Z\n";
        _challenges.Setup(c => c.GetLatestLiveByAddressAsync(Addr, Chain, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WalletAuthChallenge>
            {
                Result = new WalletAuthChallenge { Address = Addr, ChainType = Chain, Nonce = nonce, DomainMessage = msg, ExpiresAt = DateTime.UtcNow.AddMinutes(4) }
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
        _avatars.Setup(a => a.GetByAuthWalletAsync(Addr, Chain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = null });

        // A tampered client echo of the signed message must reject (domain separation).
        var r = await _mgr.VerifyAsync(Addr, Chain, "c2ln", message: "TAMPERED-MESSAGE");

        r.IsError.Should().BeTrue();
    }

    // ── AC3b: revoke-all-grants-on-claim (the residual-child-JWT cut) ──────────

    /// <summary>
    /// Wires the claim-token happy path: a single-use token resolves the target
    /// avatar, the avatar loads + echoes back on upsert. The user-side credential is
    /// a password (the simplest legal "exactly one of {password,wallet}" choice — it
    /// avoids the challenge/verify pipeline entirely so the test isolates the
    /// revoke-on-claim behaviour). Returns the claimed avatar id.
    /// </summary>
    private Guid GivenClaimTokenHappyPath()
    {
        var token = "claim-token-abc";
        var targetId = Guid.NewGuid();
        _claimTokens.Setup(t => t.GetByTokenAsync(token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WalletAuthClaimToken>
            {
                Result = new WalletAuthClaimToken { Token = token, TargetAvatarId = targetId }
            });
        // Atomic single-use redeem wins.
        _claimTokens.Setup(t => t.TryConsumeAsync(token, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        // The tenant-provisioned avatar still has a tenant owner pre-claim.
        _avatars.Setup(a => a.GetByIdAsync(targetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar>
            {
                Result = new Avatar { Id = targetId, OwnerTenantId = Guid.NewGuid() }
            });
        _avatars.Setup(a => a.UpsertAsync(It.IsAny<IAvatar>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAvatar a, CancellationToken _) => new AZOAResult<IAvatar> { Result = a });
        return targetId;
    }

    [Fact]
    public async Task Claim_RevokesAllOutstandingGrants_AC3b()
    {
        var targetId = GivenClaimTokenHappyPath();

        // Claim with a password user-side credential (no wallet fields).
        var r = await _mgr.ClaimAsync(
            authedAvatarId: null,
            claimToken: "claim-token-abc",
            newPassword: "a-strong-new-password",
            address: null, chainType: null, signature: null, message: null);

        r.IsError.Should().BeFalse();
        // THE attack closed: a tenant's still-live consent grant is revoked the moment
        // the user claims, so a residual child JWT can no longer drive the signing seam.
        // Revocation is keyed by the CLAIMED avatar id (the grantor), exactly once.
        _consentGrants.Verify(c => c.RevokeAllByGrantorAsync(
            targetId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Claim_GrantRevokeFails_StillSucceeds_ButWarns_AC3b()
    {
        GivenClaimTokenHappyPath();
        // The grant-revoke backstop FAILS (store error). The watermark (AuthNotBefore)
        // already cut the residual-token window, so the claim itself must NOT fail.
        _consentGrants.Setup(c => c.RevokeAllByGrantorAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<int> { IsError = true, Message = "store unavailable" });

        var r = await _mgr.ClaimAsync(
            authedAvatarId: null,
            claimToken: "claim-token-abc",
            newPassword: "a-strong-new-password",
            address: null, chainType: null, signature: null, message: null);

        // Claim still succeeds — the watermark is the primary cut; revoke is belt-and-
        // suspenders. But the failure IS surfaced in the message.
        r.IsError.Should().BeFalse();
        r.Result!.Token.Should().NotBeNullOrEmpty();
        (r.Message ?? string.Empty).ToLowerInvariant()
            .Should().Match(m => m.Contains("revoke") || m.Contains("watermark"));
    }
}
