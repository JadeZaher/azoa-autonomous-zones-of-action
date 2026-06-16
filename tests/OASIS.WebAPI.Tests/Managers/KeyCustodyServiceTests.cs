using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests.Managers;

/// <summary>
/// Custody resolver behaviour (custody-key-management track): ownership-checked
/// decrypt→sign→zero, platform pseudo-wallet authority, and the rotation re-wrap
/// primitive. The store is mocked (Moq); WalletKeyService is the REAL AES-256-GCM
/// service so the round-trip is genuine.
/// <para>
/// "IDOR path performs no decrypt" is proven without a Moq spy on the (concrete,
/// non-virtual) WalletKeyService: the rejected wallet carries GARBAGE ciphertext
/// that WOULD throw if decrypted. The resolver instead returns the clean ownership
/// error — structurally proving the decrypt was never reached.
/// </para>
/// </summary>
public class KeyCustodyServiceTests
{
    private const string EncKeyA = "custody-test-encryption-key-AAAA-do-not-use-in-prod";
    private const string EncKeyB = "custody-test-encryption-key-BBBB-rotated-target-key";

    private static WalletKeyService KeyService(string encryptionKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OASIS:WalletEncryptionKey"] = encryptionKey,
            })
            .Build();
        return new WalletKeyService(config);
    }

    private static IConfiguration ConfigWithPlatformMnemonic(string? mnemonic)
    {
        var dict = new Dictionary<string, string?> { ["OASIS:WalletEncryptionKey"] = EncKeyA };
        if (mnemonic is not null)
            dict[KeyCustodyService.PlatformMnemonicConfigPath] = mnemonic;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static Mock<IWalletStore> StoreReturning(IWallet? wallet, bool error = false)
    {
        var mock = new Mock<IWalletStore>(MockBehavior.Strict);
        var result = new OASISResult<IWallet>();
        if (error || wallet is null)
        {
            result.IsError = error;
            result.Result = wallet;
        }
        else
        {
            result.Result = wallet;
        }
        mock.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return mock;
    }

    private static KeyCustodyService NewService(Mock<IWalletStore> store, WalletKeyService keyService, IConfiguration? config = null)
        => new(store.Object, keyService, config ?? ConfigWithPlatformMnemonic(null));

    /// <summary>Real owned Platform wallet whose EncryptedPrivateKey decrypts to a known hex.</summary>
    private static (Wallet wallet, string clearHex) OwnedPlatformWallet(WalletKeyService keyService, Guid avatarId)
    {
        // A representative 64-byte Algorand ClearTextPrivateKey, hex-encoded — the
        // exact shape WalletKeyService.GenerateAlgorandKeypair persists.
        var raw = new byte[64];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i + 1);
        var clearHex = Convert.ToHexString(raw).ToLowerInvariant();

        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            AvatarId = avatarId,
            ChainType = "Algorand",
            Address = "TESTADDR",
            WalletType = WalletType.Platform,
            EncryptedPrivateKey = keyService.EncryptPrivateKey(clearHex),
        };
        return (wallet, clearHex);
    }

    // ─── Resolver happy path ───

    [Fact]
    public async Task WithSigningKeyAsync_owned_platform_wallet_returns_signer_result()
    {
        var keyService = KeyService(EncKeyA);
        var avatarId = Guid.NewGuid();
        var (wallet, clearHex) = OwnedPlatformWallet(keyService, avatarId);
        var svc = NewService(StoreReturning(wallet), keyService);

        byte[]? seenByKeySigner = null;
        var result = await svc.WithSigningKeyAsync(wallet.Id, avatarId, key =>
        {
            seenByKeySigner = (byte[])key.Clone();
            return Task.FromResult("SIGNED");
        });

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be("SIGNED");
        // The signer received the exact bytes the keygen persisted (round-trip).
        Convert.ToHexString(seenByKeySigner!).ToLowerInvariant().Should().Be(clearHex);
    }

    // ─── IDOR guard: no decrypt on a different avatar ───

    [Fact]
    public async Task WithSigningKeyAsync_different_avatar_errors_and_never_decrypts()
    {
        var keyService = KeyService(EncKeyA);
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();

        // GARBAGE ciphertext: if decrypt were reached it would throw a crypto
        // exception (and produce a "Decryption failed" message), not the ownership
        // error. Getting the ownership error proves decrypt was never reached.
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            AvatarId = owner,
            ChainType = "Algorand",
            WalletType = WalletType.Platform,
            EncryptedPrivateKey = "not-real-ciphertext-would-throw-if-decrypted",
        };
        var svc = NewService(StoreReturning(wallet), keyService);

        var signerCalled = false;
        var result = await svc.WithSigningKeyAsync(wallet.Id, attacker, _ =>
        {
            signerCalled = true;
            return Task.FromResult("SIGNED");
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("Wallet not owned by this avatar.");
        result.Message.Should().NotContain("Decryption failed");
        signerCalled.Should().BeFalse();
    }

    // ─── External wallets are never signable ───

    [Fact]
    public async Task WithSigningKeyAsync_external_wallet_errors()
    {
        var keyService = KeyService(EncKeyA);
        var avatarId = Guid.NewGuid();
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            AvatarId = avatarId,
            ChainType = "Algorand",
            WalletType = WalletType.External,
            EncryptedPrivateKey = "irrelevant-external-wallets-have-no-platform-key",
        };
        var svc = NewService(StoreReturning(wallet), keyService);

        var result = await svc.WithSigningKeyAsync(wallet.Id, avatarId, _ => Task.FromResult(0));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("External wallets");
    }

    [Fact]
    public async Task WithSigningKeyAsync_not_found_errors()
    {
        var svc = NewService(StoreReturning(null), KeyService(EncKeyA));
        var result = await svc.WithSigningKeyAsync(Guid.NewGuid(), Guid.NewGuid(), _ => Task.FromResult(0));
        result.IsError.Should().BeTrue();
        result.Message.Should().Be("Wallet not found.");
    }

    // ─── Zero-on-throw: a throwing signer still zeroes the key buffer ───

    [Fact]
    public async Task WithSigningKeyAsync_zeroes_key_even_when_signer_throws()
    {
        var keyService = KeyService(EncKeyA);
        var avatarId = Guid.NewGuid();
        var (wallet, _) = OwnedPlatformWallet(keyService, avatarId);
        var svc = NewService(StoreReturning(wallet), keyService);

        // Capture the live reference the resolver handed in; after the resolver's
        // finally runs, that very buffer must be all zeros.
        byte[]? handed = null;
        var result = await svc.WithSigningKeyAsync<int>(wallet.Id, avatarId, key =>
        {
            handed = key; // SAME array the resolver will zero (not a clone)
            throw new InvalidOperationException("boom");
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Signing failed");
        handed.Should().NotBeNull();
        handed!.All(b => b == 0).Should().BeTrue("the key buffer must be zeroed in finally even on signer throw");
    }

    [Fact]
    public async Task WithSigningKeyAsync_zeroes_key_on_happy_path_too()
    {
        var keyService = KeyService(EncKeyA);
        var avatarId = Guid.NewGuid();
        var (wallet, _) = OwnedPlatformWallet(keyService, avatarId);
        var svc = NewService(StoreReturning(wallet), keyService);

        byte[]? handed = null;
        var result = await svc.WithSigningKeyAsync(wallet.Id, avatarId, key =>
        {
            handed = key;
            return Task.FromResult("ok");
        });

        result.IsError.Should().BeFalse();
        handed!.All(b => b == 0).Should().BeTrue("the key buffer is zeroed after a successful sign");
    }

    // ─── CanSignAsync predicate (no decrypt) ───

    [Fact]
    public async Task CanSignAsync_owned_platform_wallet_is_true()
    {
        var keyService = KeyService(EncKeyA);
        var avatarId = Guid.NewGuid();
        var (wallet, _) = OwnedPlatformWallet(keyService, avatarId);
        var svc = NewService(StoreReturning(wallet), keyService);

        var result = await svc.CanSignAsync(wallet.Id, avatarId);
        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
    }

    [Fact]
    public async Task CanSignAsync_wrong_avatar_is_error()
    {
        var keyService = KeyService(EncKeyA);
        var (wallet, _) = OwnedPlatformWallet(keyService, Guid.NewGuid());
        var svc = NewService(StoreReturning(wallet), keyService);

        var result = await svc.CanSignAsync(wallet.Id, Guid.NewGuid());
        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
    }

    // ─── Platform pseudo-wallet: authority-gated, same surface as a user key ───

    [Fact]
    public async Task WithPlatformSigningKeyAsync_platform_context_succeeds()
    {
        var keyService = KeyService(EncKeyA);
        // Real, restorable Algorand mnemonic from the real keygen path.
        var (_, _, _, mnemonic) = keyService.GenerateKeypair("algorand");
        var config = ConfigWithPlatformMnemonic(mnemonic);
        var svc = NewService(StoreReturning(null), keyService, config);

        byte[]? handed = null;
        var result = await svc.WithPlatformSigningKeyAsync(isPlatformContext: true, key =>
        {
            handed = (byte[])key.Clone();
            return Task.FromResult("PLATFORM-SIGNED");
        });

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be("PLATFORM-SIGNED");
        handed.Should().NotBeNull();
        // Algorand2 ClearTextPrivateKey is the 32-byte Ed25519 private scalar — the
        // SAME byte[] representation a user-key resolve yields (AlgorandTransactionSigner
        // reconstructs `new Account(bytes)` from it). The signer cannot distinguish this
        // platform resolve from a user-key resolve: identical byte[] surface.
        handed!.Length.Should().Be(32);
    }

    [Fact]
    public async Task WithPlatformSigningKeyAsync_non_platform_context_errors_and_never_decrypts()
    {
        var keyService = KeyService(EncKeyA);
        var (_, _, _, mnemonic) = keyService.GenerateKeypair("algorand");
        var config = ConfigWithPlatformMnemonic(mnemonic);
        var svc = NewService(StoreReturning(null), keyService, config);

        var signerCalled = false;
        var result = await svc.WithPlatformSigningKeyAsync(isPlatformContext: false, _ =>
        {
            signerCalled = true;
            return Task.FromResult("nope");
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("platform-authority context");
        signerCalled.Should().BeFalse();
    }

    [Fact]
    public async Task WithPlatformSigningKeyAsync_no_config_errors()
    {
        var keyService = KeyService(EncKeyA);
        var svc = NewService(StoreReturning(null), keyService, ConfigWithPlatformMnemonic(null));

        var result = await svc.WithPlatformSigningKeyAsync(isPlatformContext: true, _ => Task.FromResult(0));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("No platform signing key configured");
    }

    [Fact]
    public async Task WithPlatformSigningKeyAsync_zeroes_key_on_throw()
    {
        var keyService = KeyService(EncKeyA);
        var (_, _, _, mnemonic) = keyService.GenerateKeypair("algorand");
        var svc = NewService(StoreReturning(null), keyService, ConfigWithPlatformMnemonic(mnemonic));

        byte[]? handed = null;
        var result = await svc.WithPlatformSigningKeyAsync<int>(isPlatformContext: true, key =>
        {
            handed = key;
            throw new InvalidOperationException("platform boom");
        });

        result.IsError.Should().BeTrue();
        handed!.All(b => b == 0).Should().BeTrue("platform key buffer must be zeroed even on signer throw");
    }

    // ─── Rotation re-wrap: value encrypted under key A is recoverable after rewrap under B ───

    [Fact]
    public void RewrapAsync_value_under_keyA_is_recoverable_after_rewrap_under_keyB()
    {
        var oldKeyService = KeyService(EncKeyA);
        var newKeyService = KeyService(EncKeyB);

        var clearPk = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var clearSeed = "abandon abandon abandon abandon abandon abandon abandon abandon test";

        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            AvatarId = Guid.NewGuid(),
            ChainType = "Algorand",
            WalletType = WalletType.Platform,
            EncryptedPrivateKey = oldKeyService.EncryptPrivateKey(clearPk),
            EncryptedSeedPhrase = oldKeyService.EncryptSeedPhrase(clearSeed),
        };
        var cipherUnderA = wallet.EncryptedPrivateKey;

        var svc = NewService(StoreReturning(null), oldKeyService);
        var result = svc.RewrapAsync(wallet, oldKeyService, newKeyService);

        result.IsError.Should().BeFalse(result.Message);

        // Ciphertext changed (re-wrapped under a different data-key)...
        result.Result!.EncryptedPrivateKey.Should().NotBe(cipherUnderA);

        // ...and the NEW key service recovers the original cleartext.
        newKeyService.DecryptPrivateKey(result.Result!.EncryptedPrivateKey!).Should().Be(clearPk);
        newKeyService.DecryptSeedPhrase(result.Result!.EncryptedSeedPhrase!).Should().Be(clearSeed);
    }

    [Fact]
    public void RewrapAsync_handles_wallet_with_no_seed_phrase()
    {
        var oldKeyService = KeyService(EncKeyA);
        var newKeyService = KeyService(EncKeyB);
        var clearPk = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef0";

        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            AvatarId = Guid.NewGuid(),
            WalletType = WalletType.Platform,
            EncryptedPrivateKey = oldKeyService.EncryptPrivateKey(clearPk),
            EncryptedSeedPhrase = null,
        };

        var svc = NewService(StoreReturning(null), oldKeyService);
        var result = svc.RewrapAsync(wallet, oldKeyService, newKeyService);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.EncryptedSeedPhrase.Should().BeNull();
        newKeyService.DecryptPrivateKey(result.Result!.EncryptedPrivateKey!).Should().Be(clearPk);
    }
}
