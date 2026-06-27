using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Services.Consent;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// tenant-consent-delegation C1/AC4/AC4b: proves the SINGLE custody chokepoint
/// (<see cref="KeyCustodyService"/>) consults the consent gate BEFORE any key
/// decrypt on a tenant-driven signing action — on BOTH the per-user
/// (<c>WithSigningKeyAsync(SigningContext)</c>) and platform
/// (<c>WithPlatformSigningKeyAsync(bool, SigningContext)</c>) overloads.
///
/// <para>The C1 crux: a tenant-driven sign is REJECTED even though
/// <c>wallet.AvatarId == ctx.AvatarId</c> would pass the legacy ownership IDOR
/// check. The denial happens with NO decrypt and the signer is never invoked.</para>
///
/// <para><b>AC4b sign-path enumeration.</b> Every value-signing path in the system
/// reaches a key decrypt through exactly one of these two methods (verified by the
/// recon enumeration: <c>AlgorandProvider.SignWithCustodyAsync</c> is the ONLY caller
/// of both, fed by a <see cref="SigningContext"/>). So gating here gates them all:
/// <list type="bullet">
/// <item>AllocationManager Mint  → platform op  (BuildSigningContext platformOp) → <see cref="WithPlatformSigningKeyAsync"/></item>
/// <item>AllocationManager Transfer → user op   (BuildSigningContext) → <see cref="WithSigningKeyAsync"/></item>
/// <item>FungibleTokenManager Create (ASA) → platform op → <see cref="WithPlatformSigningKeyAsync"/></item>
/// <item>Quest Grant (mint) → platform op → <see cref="WithPlatformSigningKeyAsync"/></item>
/// <item>Quest Transfer / Refund → user op → <see cref="WithSigningKeyAsync"/></item>
/// <item>Quest FungibleTokenCreate → platform op → <see cref="WithPlatformSigningKeyAsync"/></item>
/// <item>Bridge → BlockchainOperation → BuildSigningContext → one of the two</item>
/// <item>Swap → UNSIGNED (client-side); no server sign path to gate</item>
/// </list>
/// The two parameterised tests below cover both seam methods; the per-manager
/// fail-closed behaviour follows because each builds a <see cref="SigningContext"/>
/// carrying the acting tenant and routes through these methods.</para>
/// </summary>
public class KeyCustodyConsentSeamTests
{
    private const string EncKey = "consent-seam-test-encryption-key-AAAA-not-prod";
    private const string PlatformMnemonic =
        // A valid 25-word Algorand mnemonic is not needed for the DENY tests — the
        // gate rejects BEFORE any mnemonic resolution. For the ALLOW-path we only
        // assert the gate was consulted, not a real sign, so a placeholder is fine
        // and the signer delegate records that it ran.
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon " +
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon " +
        "abandon abandon abandon abandon abandon abandon invest";

    private static WalletKeyService KeyService()
        => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AZOA:WalletEncryptionKey"] = EncKey,
        }).Build());

    private static IConfiguration Config()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AZOA:WalletEncryptionKey"] = EncKey,
            [KeyCustodyService.PlatformMnemonicConfigPath] = PlatformMnemonic,
        }).Build();

    private static Mock<IWalletStore> StoreWith(IWallet wallet)
    {
        var mock = new Mock<IWalletStore>(MockBehavior.Loose);
        mock.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });
        return mock;
    }

    private static Wallet OwnedPlatformWallet(WalletKeyService keyService, Guid avatarId)
    {
        var raw = new byte[64];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i + 1);
        return new Wallet
        {
            Id = Guid.NewGuid(),
            AvatarId = avatarId,
            ChainType = "Algorand",
            Address = "TESTADDR",
            WalletType = WalletType.Platform,
            EncryptedPrivateKey = keyService.EncryptPrivateKey(Convert.ToHexString(raw).ToLowerInvariant()),
        };
    }

    private static (KeyCustodyService svc, Mock<ITenantConsentGate> gate) NewServiceWithDenyingGate(IWalletStore store)
    {
        var gate = new Mock<ITenantConsentGate>();
        gate.Setup(g => g.EnsureAllowedAsync(It.Is<SigningContext>(c => c.IsTenantDriven), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { IsError = true, Message = "no covering grant" });
        gate.Setup(g => g.EnsureAllowedAsync(It.Is<SigningContext>(c => !c.IsTenantDriven), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        var svc = new KeyCustodyService(store, KeyService(), Config(), gate.Object);
        return (svc, gate);
    }

    // ── C1: user-key path, tenant-driven, no grant → REJECTED, no decrypt ──────

    [Fact]
    public async Task WithSigningKey_TenantDriven_NoGrant_Rejected_EvenThoughOwnershipMatches()
    {
        var keyService = KeyService();
        var avatarId = Guid.NewGuid();
        var wallet = OwnedPlatformWallet(keyService, avatarId);     // wallet.AvatarId == avatarId
        var (svc, gate) = NewServiceWithDenyingGate(StoreWith(wallet).Object);

        // Tenant-driven user context: grantor == wallet owner, but no live grant.
        var ctx = SigningContext.ForUser(avatarId, wallet.Id)
            .ActingAs(Guid.NewGuid(), AzoaScopes.TransferSign, avatarId);

        var signerRan = false;
        var r = await svc.WithSigningKeyAsync<byte[]>(ctx, _ => { signerRan = true; return Task.FromResult(Array.Empty<byte>()); });

        // REJECTED even though ownership matches (the C1 proof). The signer NEVER ran
        // (no decrypt reached). The gate was consulted.
        r.IsError.Should().BeTrue();
        signerRan.Should().BeFalse();
        gate.Verify(g => g.EnsureAllowedAsync(It.Is<SigningContext>(c => c.IsTenantDriven), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WithSigningKey_UserDriven_Allowed_SignerRuns()
    {
        var keyService = KeyService();
        var avatarId = Guid.NewGuid();
        var wallet = OwnedPlatformWallet(keyService, avatarId);
        var store = new Mock<IWalletStore>(MockBehavior.Loose);
        store.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });
        var (svc, _) = NewServiceWithDenyingGate(store.Object);

        // No acting tenant → user-driven → gate allows → signer runs.
        var ctx = SigningContext.ForUser(avatarId, wallet.Id);
        var signerRan = false;
        var r = await svc.WithSigningKeyAsync<byte[]>(ctx, _ => { signerRan = true; return Task.FromResult(new byte[] { 9 }); });

        r.IsError.Should().BeFalse();
        signerRan.Should().BeTrue();
    }

    // ── AC4b: platform-key path, tenant-driven, no grant → REJECTED ────────────
    // Covers Grant / FungibleTokenCreate (platform-SIGNED yet tenant-DRIVEN).

    [Fact]
    public async Task WithPlatformSigningKey_TenantDriven_NoGrant_Rejected_NoDecrypt()
    {
        var store = new Mock<IWalletStore>(MockBehavior.Loose).Object;
        var (svc, gate) = NewServiceWithDenyingGate(store);

        var ctx = SigningContext.Platform.ActingAs(Guid.NewGuid(), AzoaScopes.GrantSign, Guid.NewGuid());

        var signerRan = false;
        var r = await svc.WithPlatformSigningKeyAsync<byte[]>(true, ctx,
            _ => { signerRan = true; return Task.FromResult(Array.Empty<byte>()); });

        r.IsError.Should().BeTrue();
        signerRan.Should().BeFalse();
        gate.Verify(g => g.EnsureAllowedAsync(It.Is<SigningContext>(c => c.IsTenantDriven), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WithPlatformSigningKey_NonTenantDriven_PassesGate()
    {
        var store = new Mock<IWalletStore>(MockBehavior.Loose).Object;
        var (svc, gate) = NewServiceWithDenyingGate(store);

        // Plain platform op (no acting tenant) → gate allows; the platform path then
        // proceeds (the actual sign may fail on the placeholder mnemonic, but the
        // gate was NOT the blocker — that's what we assert).
        var ctx = SigningContext.Platform;
        await svc.WithPlatformSigningKeyAsync<byte[]>(true, ctx, _ => Task.FromResult(new byte[] { 1 }));

        gate.Verify(g => g.EnsureAllowedAsync(It.Is<SigningContext>(c => !c.IsTenantDriven), It.IsAny<CancellationToken>()), Times.Once);
    }
}
