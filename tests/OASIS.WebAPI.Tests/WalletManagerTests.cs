using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests;

public class WalletManagerTests
{
    private readonly Mock<IWalletStore> _walletStore;
    private readonly Mock<IHolonStore> _holonStore;
    private readonly WalletManager _manager;
    private readonly WalletKeyService _keyService;
    private readonly Mock<IAlgorandFaucet> _algorandFaucet;
    private readonly IConfiguration _config;

    public WalletManagerTests()
    {
        _walletStore = new Mock<IWalletStore>();
        _holonStore = new Mock<IHolonStore>();

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OASIS:WalletEncryptionKey"] = "test-encryption-key-for-unit-tests-min-32-chars!!",
                ["Blockchain:DefaultNetwork"] = "Devnet",
                ["Blockchain:Faucet:DefaultAmount"] = "5"
            })
            .Build();
        var chainFactory = new Mock<IBlockchainProviderFactory>();
        _keyService = new WalletKeyService(_config);
        _algorandFaucet = new Mock<IAlgorandFaucet>();
        _manager = new WalletManager(_walletStore.Object, _holonStore.Object, chainFactory.Object, _keyService, _config, _algorandFaucet.Object);
    }

    [Fact]
    public async Task CreateAsync_ShouldSetAvatarIdAndSave()
    {
        _walletStore.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = Array.Empty<IWallet>() });
        _walletStore.Setup(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new OASISResult<IWallet> { Result = w });

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
                 .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = new[] { existing } });

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
                 .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = new[] { prev } });
        _walletStore.Setup(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new OASISResult<IWallet> { Result = w });

        var model = new WalletCreateModel { ChainType = "Solana", Address = "new", IsDefault = true };
        var result = await _manager.CreateAsync(model, prev.AvatarId);

        result.IsError.Should().BeFalse();
        _walletStore.Verify(p => p.UpsertAsync(It.Is<IWallet>(w => w.Id == prev.Id && !w.IsDefault), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_ShouldApplyPartialChanges()
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid(), ChainType = "Solana", Address = "addr", Label = "Old", IsDefault = false };
        _walletStore.Setup(p => p.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IWallet> { Result = wallet });
        _walletStore.Setup(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new OASISResult<IWallet> { Result = w });

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
                 .ReturnsAsync(new OASISResult<IWallet> { Result = current });
        _walletStore.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = new[] { prev } });
        _walletStore.Setup(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new OASISResult<IWallet> { Result = w });

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
                 .ReturnsAsync(new OASISResult<IWallet> { Result = wallet });

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
                 .ReturnsAsync(new OASISResult<IWallet> { Result = wallet });
        _holonStore.Setup(p => p.QueryAsync(null, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new[] { nft } });

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
                 .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = new[] { w1, w2 } });

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
                 .ReturnsAsync(new OASISResult<IWallet> { Result = wallet });
        return wallet;
    }

    [Fact]
    public async Task TopUpAsync_WalletNotFound_ReturnsError()
    {
        var id = Guid.NewGuid();
        _walletStore.Setup(p => p.GetByIdAsync(id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IWallet> { IsError = true, Result = null });

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
                ["OASIS:WalletEncryptionKey"] = "test-encryption-key-for-unit-tests-min-32-chars!!",
                ["Blockchain:DefaultNetwork"] = "Mainnet"
            })
            .Build();
        var chainFactory = new Mock<IBlockchainProviderFactory>();
        var manager = new WalletManager(_walletStore.Object, _holonStore.Object, chainFactory.Object,
            new WalletKeyService(mainnetConfig), mainnetConfig, _algorandFaucet.Object);

        var avatarId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(avatarId);

        var result = await manager.TopUpAsync(wallet.Id, 5m, avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("mainnet");
        _algorandFaucet.Verify(f => f.DispenseAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TopUpAsync_AlgorandFaucetNotConfigured_ReturnsClearError()
    {
        var avatarId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(avatarId);
        _algorandFaucet.Setup(f => f.IsConfigured).Returns(false);

        var result = await _manager.TopUpAsync(wallet.Id, 5m, avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Blockchain:Faucet:Algorand:Mnemonic");
    }

    [Fact]
    public async Task TopUpAsync_AlgorandSuccess_ReturnsTxHashAndUsesDefaultAmount()
    {
        var avatarId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(avatarId);
        _algorandFaucet.Setup(f => f.IsConfigured).Returns(true);
        _algorandFaucet.Setup(f => f.DispenseAsync(wallet.Address, 5m, It.IsAny<CancellationToken>()))
                       .ReturnsAsync("TXHASH123");

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
    public async Task TopUpAsync_AlgorandFaucetThrows_ReturnsError()
    {
        var avatarId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(avatarId);
        _algorandFaucet.Setup(f => f.IsConfigured).Returns(true);
        _algorandFaucet.Setup(f => f.DispenseAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new InvalidOperationException("algod unreachable"));

        var result = await _manager.TopUpAsync(wallet.Id, 5m, avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("algod unreachable");
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
                 .ReturnsAsync(new OASISResult<IWallet> { Result = wallet });

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
                 .ReturnsAsync(new OASISResult<IWallet> { Result = wallet });

        var result = await _manager.TopUpAsync(wallet.Id, 1m, avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not supported");
    }
}
