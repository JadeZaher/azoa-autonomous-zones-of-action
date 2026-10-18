using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Solnet.Wallet.Bip39;
using Xunit;
using AlgoAccount = Algorand.Algod.Model.Account;
using SolanaWallet = Solnet.Wallet.Wallet;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// The custody chain gap, closed at its single source (custody seam option 4): the
/// <see cref="SigningContext"/> is stamped with the settling chain ONCE, where
/// <see cref="BlockchainOperationManager"/> builds it.
/// </summary>
/// <remarks>
/// A chain-aware platform key door shipped, but every provider still called
/// <c>WithPlatformSigningKeyAsync(true, ctx, sign)</c> without naming its chain, so all of
/// them took the door's Algorand-pinned legacy path and failed closed. Stamping at the
/// build site makes every provider correct with zero provider edits and leaves none able to
/// drift — a new provider inherits the fix by existing.
/// </remarks>
public sealed class ChainStampedSigningContextTests
{
    private const string EncryptionKey = "chain-stamp-test-encryption-key-000000001";
    private const string EthereumTestPrivateKey =
        "0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";

    private static readonly string AlgorandMnemonic = new AlgoAccount().ToMnemonic();
    private static readonly string SolanaMnemonic = new Mnemonic(WordList.English, WordCount.Twelve).ToString();

    // ═══════════════════════════════════════════════════════════════════════
    // The manager stamps the settling chain onto every context it builds
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Algorand")]
    [InlineData("Solana")]
    [InlineData("Ethereum")]
    public async Task Mint_stamps_the_settling_chain_on_the_platform_context(string chainType)
    {
        var (manager, captured) = NewManagerCapturing(chainType);

        var result = await manager.ExecuteAsync(new BlockchainOperation
        {
            OperationType = "Mint",
            TokenUri = "ipfs://token",
            Amount = 1,
            AssetType = "NFT",
            Parameters = new Dictionary<string, string> { ["ChainType"] = chainType },
        });

        result.IsError.Should().BeFalse(result.Message);
        captured.Value.Should().NotBeNull("the value path must reach the provider");
        captured.Value!.Value.IsPlatform.Should().BeTrue("an ASA-create style mint is a platform op");
        captured.Value!.Value.ChainType.Should().Be(chainType);
    }

    [Theory]
    [InlineData("Algorand")]
    [InlineData("Solana")]
    public async Task Burn_stamps_the_settling_chain_on_the_platform_context(string chainType)
    {
        var (manager, captured) = NewManagerCapturing(chainType);

        await manager.ExecuteAsync(new BlockchainOperation
        {
            OperationType = "Burn",
            Parameters = new Dictionary<string, string>
            {
                ["ChainType"] = chainType,
                ["TokenId"] = "123",
                ["Amount"] = "5",
            },
        });

        captured.Value!.Value.ChainType.Should().Be(chainType);
    }

    [Fact]
    public async Task A_per_user_transfer_carries_the_chain_too_so_no_context_is_unstamped()
    {
        var userId = Guid.NewGuid();
        var walletId = Guid.NewGuid();
        var (manager, captured) = NewManagerCapturing("Solana");

        await manager.ExecuteAsync(new BlockchainOperation
        {
            OperationType = "Transfer",
            UserId = userId,
            WalletId = walletId,
            RecipientAddress = "recipient",
            Parameters = new Dictionary<string, string>
            {
                ["ChainType"] = "Solana",
                ["SourceTokenId"] = "mint",
                ["Amount"] = "7",
            },
        });

        var ctx = captured.Value!.Value;
        ctx.IsPlatform.Should().BeFalse("a transfer from a user's custodial wallet signs with that user's key");
        ctx.UserId.Should().Be(userId);
        ctx.WalletId.Should().Be(walletId);
        ctx.ChainType.Should().Be("Solana",
            "stamping every context, not only platform ones, is what makes drift impossible");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // …and a stamped context resolves THAT chain's platform key
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Algorand", 32)]
    [InlineData("Solana", 64)]
    [InlineData("Ethereum", 32)]
    public async Task A_manager_built_context_resolves_that_chains_platform_key(
        string chainType, int expectedKeyLength)
    {
        // End to end across the seam: the manager builds the context, the real custody
        // service resolves the key from it. Nothing in between re-states the chain.
        var (manager, captured) = NewManagerCapturing(chainType);
        await manager.ExecuteAsync(new BlockchainOperation
        {
            OperationType = "Mint",
            TokenUri = "ipfs://token",
            Amount = 1,
            AssetType = "NFT",
            Parameters = new Dictionary<string, string> { ["ChainType"] = chainType },
        });

        byte[]? handed = null;
        var custody = NewCustodyService();
        var result = await custody.WithPlatformSigningKeyAsync(
            isPlatformContext: true, captured.Value!.Value, key =>
            {
                handed = (byte[])key.Clone();
                return Task.FromResult("SIGNED");
            });

        result.IsError.Should().BeFalse(result.Message);
        handed!.Length.Should().Be(expectedKeyLength);
    }

    [Fact]
    public async Task A_solana_operation_no_longer_receives_the_algorand_seed()
    {
        var (manager, captured) = NewManagerCapturing("Solana");
        await manager.ExecuteAsync(new BlockchainOperation
        {
            OperationType = "Mint",
            TokenUri = "ipfs://token",
            Amount = 1,
            AssetType = "NFT",
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Solana" },
        });

        byte[]? handed = null;
        await NewCustodyService().WithPlatformSigningKeyAsync(
            isPlatformContext: true, captured.Value!.Value, key =>
            {
                handed = (byte[])key.Clone();
                return Task.FromResult(0);
            });

        handed.Should().Equal(
            new SolanaWallet(SolanaMnemonic, WordList.English).Account.PrivateKey.KeyBytes,
            "the stamped chain selects the Solana derivation, not the legacy Algorand one");
        handed.Should().NotEqual(new AlgoAccount(AlgorandMnemonic).KeyPair.ClearTextPrivateKey);
    }

    [Fact]
    public async Task An_unstamped_context_still_takes_the_pinned_legacy_path()
    {
        // The behaviour the stamp exists to avoid, kept explicit: a caller that does NOT
        // name its chain gets Algorand's key — correct only for Algorand.
        byte[]? handed = null;
        var result = await NewCustodyService().WithPlatformSigningKeyAsync(
            isPlatformContext: true, SigningContext.Platform, key =>
            {
                handed = (byte[])key.Clone();
                return Task.FromResult(0);
            });

        result.IsError.Should().BeFalse(result.Message);
        handed!.Length.Should().Be(32);
        handed.Should().Equal(new AlgoAccount(AlgorandMnemonic).KeyPair.ClearTextPrivateKey);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class Captured
    {
        public SigningContext? Value { get; set; }
    }

    private static (BlockchainOperationManager Manager, Captured Captured) NewManagerCapturing(string chainType)
    {
        var captured = new Captured();
        var provider = new Mock<IBlockchainProvider>();
        provider.Setup(p => p.ChainType).Returns(chainType);
        provider.Setup(p => p.MintAsync(
                It.IsAny<string>(), It.IsAny<RailAmount>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, RailAmount __, string ___, string ____, SigningContext? ctx, CancellationToken _____) =>
                captured.Value = ctx)
            .ReturnsAsync(new AZOAResult<string> { Result = "tx" });
        provider.Setup(p => p.BurnAsync(
                It.IsAny<string>(), It.IsAny<RailAmount>(), It.IsAny<string>(),
                It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, RailAmount __, string ___, SigningContext? ctx, CancellationToken ____) =>
                captured.Value = ctx)
            .ReturnsAsync(new AZOAResult<string> { Result = "tx" });
        provider.Setup(p => p.TransferAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<RailAmount>(),
                It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string __, string ___, RailAmount ____, SigningContext? ctx, CancellationToken _____) =>
                captured.Value = ctx)
            .ReturnsAsync(new AZOAResult<string> { Result = "tx" });

        var store = new Mock<IBlockchainOperationStore>();
        store.Setup(s => s.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IBlockchainOperation op, CancellationToken _) =>
                new AZOAResult<IBlockchainOperation> { Result = op });

        var factory = new BlockchainProviderFactory(
            new[] { new BlockchainProviderRegistration(chainType, () => provider.Object) },
            ChainEnabledConfig(chainType));

        return (new BlockchainOperationManager(store.Object, factory, new FakeIdempotencyStore(), valueAccess: new AZOA.WebAPI.Services.Kyc.ValueAccessService(new ApprovedRealValueKycGate())), captured);
    }

    private static IConfiguration ChainEnabledConfig(string chainType) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultChain"] = chainType,
                ["Blockchain:Chains:0:ChainType"] = chainType,
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            })
            .Build();

    private static KeyCustodyService NewCustodyService()
    {
        var walletStore = new Mock<IWalletStore>();
        walletStore.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IWallet> { Result = null });

        var consentGate = new Mock<ITenantConsentGate>();
        consentGate.Setup(g => g.EnsureAllowedAsync(It.IsAny<SigningContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });

        var platformConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = EncryptionKey,
                ["AZOA:Algorand:PlatformMnemonic"] = AlgorandMnemonic,
                ["AZOA:Solana:PlatformMnemonic"] = SolanaMnemonic,
                ["AZOA:Ethereum:PlatformPrivateKey"] = EthereumTestPrivateKey,
            })
            .Build();

        var keyServiceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = EncryptionKey,
            })
            .Build();

        return new KeyCustodyService(
            walletStore.Object,
            new WalletKeyService(keyServiceConfig),
            platformConfig,
            consentGate.Object,
            PlatformKeyDerivationRegistry.Default);
    }
}
