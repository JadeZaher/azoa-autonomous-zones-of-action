using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Solnet.Wallet.Bip39;
using AlgoAccount = Algorand.Algod.Model.Account;
using SolanaWallet = Solnet.Wallet.Wallet;

namespace AZOA.WebAPI.Tests.Custody;

/// <summary>
/// The chain-aware platform signing door (<c>Services/Custody/AGENTS.md</c>
/// §platform-key-derivation). Before this, the door read
/// <c>AZOA:Algorand:PlatformMnemonic</c> and applied Algorand's import to every chain,
/// so a Solana platform op received a 32-byte Algorand seed. These tests pin the four
/// properties that make the fix safe: right key per chain, fail closed on an
/// unregistered chain, never a platform key for a per-user request, and no secret or
/// key byte in any message.
/// </summary>
public sealed class ChainAwarePlatformSigningTests
{
    private const string EncryptionKey = "custody-chain-aware-test-encryption-key-0001";

    /// <summary>Well-known Ethereum documentation test key; deterministic, worthless.</summary>
    private const string EthereumTestPrivateKey =
        "0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";

    private static readonly string AlgorandMnemonic = new AlgoAccount().ToMnemonic();
    private static readonly string SolanaMnemonic = new Mnemonic(WordList.English, WordCount.Twelve).ToString();

    // ─── Correct key, per chain ───

    [Theory]
    [InlineData("Algorand", 32)]
    [InlineData("Solana", 64)]
    [InlineData("Ethereum", 32)]
    public async Task Named_chain_resolves_that_chains_key_shape(string chainType, int expectedLength)
    {
        var service = NewService(FullPlatformConfig());

        byte[]? handed = null;
        var result = await service.WithPlatformSigningKeyAsync(
            chainType, isPlatformContext: true, SigningContext.Platform, key =>
            {
                handed = (byte[])key.Clone();
                return Task.FromResult("SIGNED");
            });

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be("SIGNED");
        handed!.Length.Should().Be(expectedLength);
    }

    [Fact]
    public async Task Solana_platform_op_gets_the_solana_secret_not_the_algorand_seed()
    {
        var service = NewService(FullPlatformConfig());

        byte[]? handed = null;
        var result = await service.WithPlatformSigningKeyAsync(
            "Solana", isPlatformContext: true, SigningContext.Platform, key =>
            {
                handed = (byte[])key.Clone();
                return Task.FromResult(0);
            });

        result.IsError.Should().BeFalse(result.Message);
        handed.Should().Equal(
            new SolanaWallet(SolanaMnemonic, WordList.English).Account.PrivateKey.KeyBytes,
            "the Solana door must import AZOA:Solana:PlatformMnemonic, not the Algorand mnemonic");
        handed.Should().NotEqual(new AlgoAccount(AlgorandMnemonic).KeyPair.ClearTextPrivateKey);
    }

    [Fact]
    public async Task Signing_context_chain_stamp_routes_the_legacy_overload_to_the_right_chain()
    {
        var service = NewService(FullPlatformConfig());

        byte[]? handed = null;
        var result = await service.WithPlatformSigningKeyAsync(
            isPlatformContext: true, SigningContext.PlatformFor("Solana"), key =>
            {
                handed = (byte[])key.Clone();
                return Task.FromResult(0);
            });

        result.IsError.Should().BeFalse(result.Message);
        handed!.Length.Should().Be(64);
    }

    [Fact]
    public async Task Unstamped_context_keeps_the_pinned_legacy_algorand_behaviour()
    {
        var service = NewService(FullPlatformConfig());

        byte[]? handed = null;
        var result = await service.WithPlatformSigningKeyAsync(
            isPlatformContext: true, SigningContext.Platform, key =>
            {
                handed = (byte[])key.Clone();
                return Task.FromResult(0);
            });

        result.IsError.Should().BeFalse(result.Message);
        handed.Should().Equal(new AlgoAccount(AlgorandMnemonic).KeyPair.ClearTextPrivateKey);
    }

    // ─── Fail closed: unregistered chain, blank chain, missing config ───

    [Fact]
    public async Task Unregistered_chain_fails_closed_naming_the_chain_and_never_signs()
    {
        // Registry deliberately knows Algorand ONLY; asking for Solana must not silently
        // produce Algorand's key.
        var service = NewService(
            FullPlatformConfig(),
            new PlatformKeyDerivationRegistry([new AlgorandPlatformKeyDerivation()]));

        var signerCalled = false;
        var result = await service.WithPlatformSigningKeyAsync(
            "Solana", isPlatformContext: true, SigningContext.Platform, _ =>
            {
                signerCalled = true;
                return Task.FromResult(0);
            });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Solana", "a fail-closed error must name the chain that has no door");
        result.Message.Should().Contain(
            "platform signing key derivation is registered", "the cause must be stated, not inferred");
        result.Message.Should().Contain("Algorand", "the reachable chains help the operator act on it");
        signerCalled.Should().BeFalse();
    }

    [Theory]
    [InlineData("Bitcoin")]
    [InlineData("Cardano")]
    [InlineData("Simulated")]
    public async Task Chain_with_no_derivation_never_falls_through_to_algorand(string chainType)
    {
        var service = NewService(FullPlatformConfig());

        byte[]? handed = null;
        var result = await service.WithPlatformSigningKeyAsync(
            chainType, isPlatformContext: true, SigningContext.Platform, key =>
            {
                handed = (byte[])key.Clone();
                return Task.FromResult(0);
            });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain(chainType);
        handed.Should().BeNull("no key of any shape may reach a signer for an unregistered chain");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_chain_fails_closed_rather_than_guessing(string chainType)
    {
        var service = NewService(FullPlatformConfig());

        var signerCalled = false;
        var result = await service.WithPlatformSigningKeyAsync(
            chainType, isPlatformContext: true, SigningContext.Platform, _ =>
            {
                signerCalled = true;
                return Task.FromResult(0);
            });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("No chain was named");
        signerCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Registered_chain_with_no_configured_secret_names_the_missing_config_key()
    {
        // Algorand configured, Solana not.
        var service = NewService(Config(new Dictionary<string, string?>
        {
            ["AZOA:WalletEncryptionKey"] = EncryptionKey,
            ["AZOA:Algorand:PlatformMnemonic"] = AlgorandMnemonic,
        }));

        var result = await service.WithPlatformSigningKeyAsync(
            "Solana", isPlatformContext: true, SigningContext.Platform, _ => Task.FromResult(0));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("No platform signing key configured");
        result.Message.Should().Contain("Solana");
        result.Message.Should().Contain("AZOA:Solana:PlatformMnemonic");
    }

    [Fact]
    public async Task Non_platform_authority_is_still_refused_before_any_derivation()
    {
        var service = NewService(FullPlatformConfig());

        var signerCalled = false;
        var result = await service.WithPlatformSigningKeyAsync(
            "Algorand", isPlatformContext: false, SigningContext.Platform, _ =>
            {
                signerCalled = true;
                return Task.FromResult(0);
            });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("platform-authority context");
        signerCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Denied_consent_still_fails_before_any_derivation_on_the_chain_explicit_door()
    {
        var service = NewService(FullPlatformConfig(), consentGate: DenyingConsentGate());

        var signerCalled = false;
        var result = await service.WithPlatformSigningKeyAsync(
            "Solana", isPlatformContext: true,
            SigningContext.PlatformFor("Solana").ActingAs(Guid.NewGuid(), "nft:mint", Guid.NewGuid()),
            _ =>
            {
                signerCalled = true;
                return Task.FromResult(0);
            });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("no covering grant");
        signerCalled.Should().BeFalse();
    }

    // ─── A per-user request never reaches the platform key ───

    [Fact]
    public async Task Per_user_resolve_with_a_missing_wallet_never_falls_back_to_the_platform_key()
    {
        var service = NewService(FullPlatformConfig(), store: StoreReturning(null));

        byte[]? handed = null;
        var result = await service.WithSigningKeyAsync(Guid.NewGuid(), Guid.NewGuid(), key =>
        {
            handed = (byte[])key.Clone();
            return Task.FromResult(0);
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Wallet not found");
        handed.Should().BeNull("a per-user context that cannot be resolved must never receive a platform key");
    }

    [Fact]
    public async Task Per_user_resolve_owned_by_another_user_never_falls_back_to_the_platform_key()
    {
        var keyService = NewKeyService();
        var wallet = OwnedPlatformWallet(keyService, Guid.NewGuid());
        var service = NewService(FullPlatformConfig(), store: StoreReturning(wallet), keyService: keyService);

        byte[]? handed = null;
        var result = await service.WithSigningKeyAsync(wallet.Id, Guid.NewGuid(), key =>
        {
            handed = (byte[])key.Clone();
            return Task.FromResult(0);
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not owned by this user");
        handed.Should().BeNull();
    }

    [Fact]
    public async Task Per_user_resolve_on_a_chain_stamped_context_still_uses_the_wallet_key()
    {
        var keyService = NewKeyService();
        var userId = Guid.NewGuid();
        var wallet = OwnedPlatformWallet(keyService, userId);
        var service = NewService(FullPlatformConfig(), store: StoreReturning(wallet), keyService: keyService);

        byte[]? handed = null;
        var ctx = SigningContext.ForUser(userId, wallet.Id).ForChain("Solana");
        var result = await service.WithSigningKeyAsync(ctx, key =>
        {
            handed = (byte[])key.Clone();
            return Task.FromResult(0);
        });

        result.IsError.Should().BeFalse(result.Message);
        handed.Should().NotBeNull();
        handed.Should().NotEqual(
            new SolanaWallet(SolanaMnemonic, WordList.English).Account.PrivateKey.KeyBytes,
            "a chain stamp routes the PLATFORM door only; a per-user key still comes from the wallet record");
        handed.Should().NotEqual(new AlgoAccount(AlgorandMnemonic).KeyPair.ClearTextPrivateKey);
    }

    // ─── Nothing derived from a secret escapes in a message, exception, or key buffer ───

    [Theory]
    [InlineData("Algorand", "AZOA:Algorand:PlatformMnemonic")]
    [InlineData("Solana", "AZOA:Solana:PlatformMnemonic")]
    [InlineData("Ethereum", "AZOA:Ethereum:PlatformPrivateKey")]
    public async Task Malformed_secret_error_names_the_config_key_but_never_echoes_the_value(
        string chainType, string configPath)
    {
        const string Sentinel = "zzsentinelsecretzz";
        var service = NewService(Config(new Dictionary<string, string?>
        {
            ["AZOA:WalletEncryptionKey"] = EncryptionKey,
            [configPath] = $"0x{Sentinel} {Sentinel} {Sentinel}",
        }));

        var result = await service.WithPlatformSigningKeyAsync(
            chainType, isPlatformContext: true, SigningContext.Platform, _ => Task.FromResult(0));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain(configPath);
        result.Message.Should().NotContain(Sentinel, "a malformed secret must never be echoed back");
        result.Exception.Should().BeNull(
            "chain SDKs put mnemonic words in exception messages, and a captured exception is "
            + "serialized through AZOAResult.Detail when debug errors are on");
    }

    [Theory]
    [InlineData("Algorand")]
    [InlineData("Solana")]
    [InlineData("Ethereum")]
    public async Task Signer_throw_zeroes_the_key_and_leaks_no_key_bytes(string chainType)
    {
        var service = NewService(FullPlatformConfig());

        byte[]? handed = null;
        byte[]? snapshot = null;
        var result = await service.WithPlatformSigningKeyAsync<int>(
            chainType, isPlatformContext: true, SigningContext.Platform, key =>
            {
                handed = key;
                snapshot = (byte[])key.Clone();
                throw new InvalidOperationException("platform boom");
            });

        result.IsError.Should().BeTrue();
        handed!.All(b => b == 0).Should().BeTrue("the platform key must be zeroed even on signer throw");
        result.Message.Should().NotContain(Convert.ToHexString(snapshot!));
        result.Message.Should().NotContain(Convert.ToHexString(snapshot!).ToLowerInvariant());
        result.Message.Should().NotContain(Convert.ToBase64String(snapshot!));
    }

    [Theory]
    [InlineData("Algorand", "AZOA:Algorand:PlatformMnemonic")]
    [InlineData("Solana", "AZOA:Solana:PlatformMnemonic")]
    [InlineData("Ethereum", "AZOA:Ethereum:PlatformPrivateKey")]
    public async Task Successful_resolve_puts_no_secret_in_the_result_message(string chainType, string configPath)
    {
        var config = FullPlatformConfig();
        var secret = config[configPath]!;
        var service = NewService(config);

        var result = await service.WithPlatformSigningKeyAsync(
            chainType, isPlatformContext: true, SigningContext.Platform, _ => Task.FromResult("ok"));

        result.IsError.Should().BeFalse(result.Message);
        result.Message.Should().NotContain(secret);
        foreach (var word in secret.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            result.Message.Should().NotContain(word);
    }

    // ─── DI: the scoped chokepoint still resolves, with no captive dependency ───

    [Fact]
    public async Task Scoped_custody_service_resolves_from_the_real_container_and_uses_the_default_registry()
    {
        // Mirrors Program.cs: IWalletStore + ITenantConsentGate scoped, WalletKeyService
        // singleton, IKeyCustodyService scoped. The derivation registry is deliberately
        // NOT registered — it is a static default — so this proves the four-argument
        // constructor is still the one the container selects, and that scope validation
        // (which would flag a singleton capturing the scoped custody service) passes.
        var services = new ServiceCollection();
        services.AddSingleton(FullPlatformConfig());
        services.AddSingleton(NewKeyService());
        services.AddScoped(_ => StoreReturning(null).Object);
        services.AddScoped(_ => AllowAllConsentGate());
        services.AddScoped<IKeyCustodyService, KeyCustodyService>();

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        await using var scope = provider.CreateAsyncScope();
        var custody = scope.ServiceProvider.GetRequiredService<IKeyCustodyService>();

        byte[]? handed = null;
        var result = await custody.WithPlatformSigningKeyAsync(
            "Solana", isPlatformContext: true, SigningContext.Platform, key =>
            {
                handed = (byte[])key.Clone();
                return Task.FromResult(0);
            });

        result.IsError.Should().BeFalse(result.Message);
        handed!.Length.Should().Be(64, "the container-built service must carry the default per-chain registry");
    }

    // ─── Helpers ───

    private static IConfiguration FullPlatformConfig() => Config(new Dictionary<string, string?>
    {
        ["AZOA:WalletEncryptionKey"] = EncryptionKey,
        ["AZOA:Algorand:PlatformMnemonic"] = AlgorandMnemonic,
        ["AZOA:Solana:PlatformMnemonic"] = SolanaMnemonic,
        ["AZOA:Ethereum:PlatformPrivateKey"] = EthereumTestPrivateKey,
    });

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static WalletKeyService NewKeyService() => new(Config(new Dictionary<string, string?>
    {
        ["AZOA:WalletEncryptionKey"] = EncryptionKey,
    }));

    private static KeyCustodyService NewService(
        IConfiguration config,
        PlatformKeyDerivationRegistry? derivations = null,
        Mock<IWalletStore>? store = null,
        WalletKeyService? keyService = null,
        ITenantConsentGate? consentGate = null)
        => new(
            (store ?? StoreReturning(null)).Object,
            keyService ?? NewKeyService(),
            config,
            consentGate ?? AllowAllConsentGate(),
            derivations ?? PlatformKeyDerivationRegistry.Default);

    private static Mock<IWalletStore> StoreReturning(IWallet? wallet)
    {
        var mock = new Mock<IWalletStore>();
        mock.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });
        return mock;
    }

    private static IWallet OwnedPlatformWallet(WalletKeyService keyService, Guid userId)
    {
        var (_, privateKeyHex, address, _) = keyService.GenerateKeypair("algorand");
        return new Wallet
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            WalletType = WalletType.Platform,
            ChainType = "Algorand",
            Address = address,
            EncryptedPrivateKey = keyService.EncryptPrivateKey(privateKeyHex),
        };
    }

    private static ITenantConsentGate AllowAllConsentGate()
    {
        var mock = new Mock<ITenantConsentGate>();
        mock.Setup(g => g.EnsureAllowedAsync(It.IsAny<SigningContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        return mock.Object;
    }

    private static ITenantConsentGate DenyingConsentGate()
    {
        var mock = new Mock<ITenantConsentGate>();
        mock.Setup(g => g.EnsureAllowedAsync(It.IsAny<SigningContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool>
            {
                IsError = true,
                Message = "This tenant-driven signing action has no covering grant.",
            });
        return mock.Object;
    }
}
