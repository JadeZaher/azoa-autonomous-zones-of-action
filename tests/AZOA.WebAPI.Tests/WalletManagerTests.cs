using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests;

public class WalletManagerTests
{
    private readonly Mock<IWalletStore> _walletStore;
    private readonly Mock<IHolonStore> _holonStore;
    private readonly Mock<IBlockchainProviderFactory> _chainFactory;
    private readonly WalletManager _manager;
    private readonly WalletKeyService _keyService;
    private readonly IConfiguration _config;

    public WalletManagerTests()
    {
        _walletStore = new Mock<IWalletStore>();
        _holonStore = new Mock<IHolonStore>();

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "test-encryption-key-for-unit-tests-min-32-chars!!",
                ["Blockchain:DefaultNetwork"] = "Devnet",
                ["Blockchain:Faucet:DefaultAmount"] = "5"
            })
            .Build();
        _chainFactory = new Mock<IBlockchainProviderFactory>();
        _keyService = new WalletKeyService(_config);
        _manager = new WalletManager(_walletStore.Object, _holonStore.Object, _chainFactory.Object, _keyService, _config);
    }

    /// <summary>
    /// Faucet operations are provider-scoped: stub a provider that the factory hands
    /// out for <paramref name="chainType"/>, with its faucet capability + dispense
    /// outcome under the test's control.
    /// </summary>
    private Mock<IBlockchainProvider> GivenFaucetProvider(
        string chainType, bool supportsFaucet = true)
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.SetupGet(p => p.ChainType).Returns(chainType);
        provider.SetupGet(p => p.SupportsFaucet).Returns(supportsFaucet);
        _chainFactory
            .Setup(f => f.GetProvider(chainType, It.IsAny<ChainNetwork>()))
            .Returns(provider.Object);
        return provider;
    }

    [Fact]
    public async Task CreateAsync_ShouldSetAvatarIdAndSave()
    {
        _walletStore.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = Array.Empty<IWallet>() });
        _walletStore.Setup(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new AZOAResult<IWallet> { Result = w });

        var model = new WalletCreateModel { ChainType = "Solana", Address = "addr1", IsDefault = false };
        var avatarId = Guid.NewGuid();

        var result = await _manager.CreateAsync(model, avatarId);

        result.IsError.Should().BeFalse();
        result.Result!.AvatarId.Should().Be(avatarId);
        result.Result.Address.Should().Be("addr1");
    }

    [Fact]
    public async Task CreateAsync_DuplicateAddressPerChain_ReturnsError()
    {
        var existing = new Wallet { Id = Guid.NewGuid(), ChainType = "Solana", Address = "addr1" };
        _walletStore.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { existing } });

        var model = new WalletCreateModel { ChainType = "Solana", Address = "addr1" };
        var result = await _manager.CreateAsync(model, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("already exists");
    }

    [Fact]
    public async Task CreateAsync_WithDefault_ShouldUnsetPreviousDefault()
    {
        var prev = new Wallet { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid(), ChainType = "Solana", Address = "old", IsDefault = true };
        _walletStore.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { prev } });
        _walletStore.Setup(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new AZOAResult<IWallet> { Result = w });

        var model = new WalletCreateModel { ChainType = "Solana", Address = "new", IsDefault = true };
        var result = await _manager.CreateAsync(model, prev.AvatarId);

        result.IsError.Should().BeFalse();
        _walletStore.Verify(p => p.UpsertAsync(It.Is<IWallet>(w => w.Id == prev.Id && !w.IsDefault), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BootstrapWalletAsync_RetryReturnsSameCreateOnlyWalletWithoutSerializingKeyMaterial()
    {
        var avatarId = Guid.NewGuid();
        IWallet? persisted = null;
        _walletStore.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => persisted is not null && persisted.Id == id
                ? AZOAResult<IWallet>.Success(persisted)
                : AZOAResult<IWallet>.Failure("Wallet not found."));
        _walletStore.Setup(s => s.CreateIfAbsentAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IWallet wallet, CancellationToken _) =>
            {
                persisted ??= wallet;
                return AZOAResult<IWallet>.Success(persisted);
            });

        var request = new WalletGenerateRequest { ChainType = "Algorand", IsDefault = false };
        var first = await _manager.BootstrapWalletAsync(request, avatarId);
        var retry = await _manager.BootstrapWalletAsync(request, avatarId);

        first.IsError.Should().BeFalse();
        retry.IsError.Should().BeFalse();
        retry.Result!.Id.Should().Be(first.Result!.Id);
        retry.Result.Address.Should().Be(first.Result.Address);
        retry.Result.AvatarId.Should().Be(avatarId);
        retry.Result.WalletType.Should().Be(WalletType.Platform);
        retry.Result.EncryptedSeedPhrase.Should().BeNull();
        _walletStore.Verify(s => s.CreateIfAbsentAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()), Times.Once);

        var json = System.Text.Json.JsonSerializer.Serialize(first.Result);
        json.Should().NotContain("EncryptedPrivateKey");
        json.Should().NotContain("EncryptedSeedPhrase");
    }

    [Fact]
    public async Task BootstrapWalletAsync_DeterministicIdCollisionWithDifferentOwnerFailsClosed()
    {
        _walletStore.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IWallet>.Success(new Wallet
            {
                AvatarId = Guid.NewGuid(),
                ChainType = "Algorand",
                WalletType = WalletType.Platform
            }));

        var result = await _manager.BootstrapWalletAsync(
            new WalletGenerateRequest { ChainType = "Algorand" }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("different wallet");
        _walletStore.Verify(s => s.CreateIfAbsentAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_ShouldApplyPartialChanges()
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid(), ChainType = "Solana", Address = "addr", Label = "Old", IsDefault = false };
        _walletStore.Setup(p => p.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });
        _walletStore.Setup(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new AZOAResult<IWallet> { Result = w });

        var result = await _manager.UpdateAsync(wallet.Id, new WalletUpdateModel { Label = "New" }, wallet.AvatarId);

        result.IsError.Should().BeFalse();
        result.Result!.Label.Should().Be("New");
        result.Result.IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task SetDefaultAsync_ShouldSwapDefaultFlag()
    {
        var avatarId = Guid.NewGuid();
        var prev = new Wallet { Id = Guid.NewGuid(), AvatarId = avatarId, ChainType = "Solana", Address = "old", IsDefault = true };
        var current = new Wallet { Id = Guid.NewGuid(), AvatarId = avatarId, ChainType = "Solana", Address = "new", IsDefault = false };

        _walletStore.Setup(p => p.GetByIdAsync(current.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = current });
        _walletStore.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { prev } });
        _walletStore.Setup(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new AZOAResult<IWallet> { Result = w });

        var result = await _manager.SetDefaultAsync(avatarId, current.Id);

        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        current.IsDefault.Should().BeTrue();
        _walletStore.Verify(p => p.UpsertAsync(It.Is<IWallet>(w => w.Id == prev.Id && !w.IsDefault), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetDefaultAsync_WrongAvatar_ReturnsError()
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid(), ChainType = "Solana", Address = "addr" };
        _walletStore.Setup(p => p.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });

        var result = await _manager.SetDefaultAsync(Guid.NewGuid(), wallet.Id);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not owned");
    }

    [Fact]
    public async Task GetPortfolioAsync_ShouldReturnStubWithNfts()
    {
        var avatarId = Guid.NewGuid();
        var wallet = new Wallet { Id = Guid.NewGuid(), AvatarId = avatarId, ChainType = "Solana", Address = "addr1" };
        var nft = new Holon { Id = Guid.NewGuid(), AvatarId = avatarId, AssetType = "NFT", Name = "MyNFT", TokenId = "123", Metadata = new Dictionary<string, string> { ["image"] = "ipfs://img" } };

        _walletStore.Setup(p => p.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });
        _holonStore.Setup(p => p.QueryAsync(null, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { nft } });

        var result = await _manager.GetPortfolioAsync(wallet.Id, avatarId);

        result.IsError.Should().BeFalse();
        result.Result!.WalletId.Should().Be(wallet.Id);
        result.Result.Symbol.Should().Be("SOL");
        result.Result.Nfts.Should().HaveCount(1);
        result.Result.Nfts.First().Name.Should().Be("MyNFT");
    }

    [Fact]
    public async Task QueryAsync_WithFilters_ShouldReturnFiltered()
    {
        var w1 = new Wallet { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid(), ChainType = "Solana", IsDefault = true };
        var w2 = new Wallet { Id = Guid.NewGuid(), AvatarId = w1.AvatarId, ChainType = "Algorand", IsDefault = false };

        _walletStore.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { w1, w2 } });

        var result = await _manager.QueryAsync(new WalletQueryRequest { AvatarId = w1.AvatarId, IsDefault = true }, w1.AvatarId);

        result.IsError.Should().BeFalse();
        result.Result.Should().ContainSingle(w => w.Id == w1.Id);
    }

    // ─── TopUpAsync (faucet) ───

    private Wallet GivenOwnedAlgorandWallet(Guid avatarId)
    {
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            AvatarId = avatarId,
            ChainType = "Algorand",
            Address = "RECIPIENTADDRESSALGORAND234567ABCDEFGHIJKLMNOPQRSTUVWXYZ23",
            WalletType = WalletType.Platform
        };
        _walletStore.Setup(p => p.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });
        return wallet;
    }

    [Fact]
    public async Task TopUpAsync_WalletNotFound_ReturnsError()
    {
        var id = Guid.NewGuid();
        _walletStore.Setup(p => p.GetByIdAsync(id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { IsError = true, Result = null });

        var result = await _manager.TopUpAsync(id, null, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task TopUpAsync_WrongAvatar_ReturnsError()
    {
        var wallet = GivenOwnedAlgorandWallet(Guid.NewGuid());

        var result = await _manager.TopUpAsync(wallet.Id, null, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not owned");
    }

    [Fact]
    public async Task TopUpAsync_OnMainnet_IsHardBlocked()
    {
        var mainnetConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "test-encryption-key-for-unit-tests-min-32-chars!!",
                ["Blockchain:DefaultNetwork"] = "Mainnet"
            })
            .Build();
        var chainFactory = new Mock<IBlockchainProviderFactory>();
        var provider = new Mock<IBlockchainProvider>();
        chainFactory.Setup(f => f.GetProvider("Algorand", It.IsAny<ChainNetwork>())).Returns(provider.Object);
        var manager = new WalletManager(_walletStore.Object, _holonStore.Object, chainFactory.Object,
            new WalletKeyService(mainnetConfig), mainnetConfig);

        var avatarId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(avatarId);

        var result = await manager.TopUpAsync(wallet.Id, 5m, avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("mainnet");
        // Mainnet is hard-blocked BEFORE the provider faucet path is ever reached.
        provider.Verify(p => p.DispenseFromFaucetAsync(
            It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TopUpAsync_AlgorandFaucetNotConfigured_ReturnsClearError()
    {
        var avatarId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(avatarId);
        var provider = GivenFaucetProvider("Algorand");
        provider
            .Setup(p => p.DispenseFromFaucetAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<FaucetDispenseResult>
            {
                IsError = true,
                Message = "Algorand faucet is not configured (set Blockchain:Faucet:Algorand:Mnemonic)."
            });

        var result = await _manager.TopUpAsync(wallet.Id, 5m, avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Blockchain:Faucet:Algorand:Mnemonic");
    }

    [Fact]
    public async Task TopUpAsync_AlgorandSuccess_ReturnsTxHashAndUsesDefaultAmount()
    {
        var avatarId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(avatarId);
        var provider = GivenFaucetProvider("Algorand");
        provider
            .Setup(p => p.DispenseFromFaucetAsync(
                wallet.Address, 5m, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<FaucetDispenseResult>
            {
                Result = new FaucetDispenseResult("TXHASH123", IsClientSide: false, "Dispensed 5 test ALGO.")
            });

        var result = await _manager.TopUpAsync(wallet.Id, null, avatarId);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
        var payload = result.Result!.GetType();
        payload.GetProperty("txHash")!.GetValue(result.Result).Should().Be("TXHASH123");
        payload.GetProperty("amount")!.GetValue(result.Result).Should().Be(5m);
        payload.GetProperty("chain")!.GetValue(result.Result).Should().Be("Algorand");
        payload.GetProperty("network")!.GetValue(result.Result).Should().Be("Devnet");
    }

    [Fact]
    public async Task TopUpAsync_AlgorandFaucetErrors_ReturnsError()
    {
        var avatarId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(avatarId);
        var provider = GivenFaucetProvider("Algorand");
        provider
            .Setup(p => p.DispenseFromFaucetAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<FaucetDispenseResult>
            {
                IsError = true,
                Message = "Algorand faucet failed: algod unreachable"
            });

        var result = await _manager.TopUpAsync(wallet.Id, 5m, avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("algod unreachable");
    }

    [Fact]
    public async Task TopUpAsync_ProviderThrows_ReturnsErrorNotThrows()
    {
        var avatarId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(avatarId);
        var provider = GivenFaucetProvider("Algorand");
        provider
            .Setup(p => p.DispenseFromFaucetAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _manager.TopUpAsync(wallet.Id, 5m, avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("boom");
    }

    [Fact]
    public async Task TopUpAsync_Solana_ReturnsClientSideMessageWithoutError()
    {
        var avatarId = Guid.NewGuid();
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            AvatarId = avatarId,
            ChainType = "Solana",
            Address = "SoLaNaAddr1111111111111111111111111111111111",
            WalletType = WalletType.Platform
        };
        _walletStore.Setup(p => p.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });
        var provider = GivenFaucetProvider("Solana");
        provider
            .Setup(p => p.DispenseFromFaucetAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<FaucetDispenseResult>
            {
                Result = new FaucetDispenseResult(null, IsClientSide: true,
                    "Solana devnet/testnet top-up is performed client-side via RPC airdrop (requestAirdrop).")
            });

        var result = await _manager.TopUpAsync(wallet.Id, 1m, avatarId);

        result.IsError.Should().BeFalse();
        result.Message.Should().Contain("client-side");
    }

    [Fact]
    public async Task TopUpAsync_UnsupportedChain_ReturnsError()
    {
        var avatarId = Guid.NewGuid();
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            AvatarId = avatarId,
            ChainType = "Ethereum",
            Address = "0xabc",
            WalletType = WalletType.Platform
        };
        _walletStore.Setup(p => p.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });
        // A provider with no faucet path (SupportsFaucet == false) ⇒ "not supported".
        GivenFaucetProvider("Ethereum", supportsFaucet: false);

        var result = await _manager.TopUpAsync(wallet.Id, 1m, avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not supported");
    }
}
