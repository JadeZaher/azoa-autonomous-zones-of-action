using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests;

public class WalletManagerTests
{
    private readonly Mock<IOASISStorageProvider> _provider;
    private readonly ProviderContext _providerContext;
    private readonly WalletManager _manager;

    public WalletManagerTests()
    {
        _provider = new Mock<IOASISStorageProvider>();
        _provider.Setup(p => p.ProviderName).Returns("InMemory");

        var config = new ConfigurationBuilder().Build();
        _providerContext = new ProviderContext(new[] { _provider.Object }, config, null);
        var chainFactory = new Mock<IBlockchainProviderFactory>();
        _manager = new WalletManager(_providerContext, chainFactory.Object);
    }

    [Fact]
    public async Task CreateAsync_ShouldSetAvatarIdAndSave()
    {
        _provider.Setup(p => p.LoadAllWalletsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = Array.Empty<IWallet>() });
        _provider.Setup(p => p.SaveWalletAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
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
        _provider.Setup(p => p.LoadAllWalletsAsync(It.IsAny<CancellationToken>()))
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
        _provider.Setup(p => p.LoadAllWalletsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = new[] { prev } });
        _provider.Setup(p => p.SaveWalletAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new OASISResult<IWallet> { Result = w });

        var model = new WalletCreateModel { ChainType = "Solana", Address = "new", IsDefault = true };
        var result = await _manager.CreateAsync(model, prev.AvatarId);

        result.IsError.Should().BeFalse();
        _provider.Verify(p => p.SaveWalletAsync(It.Is<IWallet>(w => w.Id == prev.Id && !w.IsDefault), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_ShouldApplyPartialChanges()
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid(), ChainType = "Solana", Address = "addr", Label = "Old", IsDefault = false };
        _provider.Setup(p => p.LoadWalletAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IWallet> { Result = wallet });
        _provider.Setup(p => p.SaveWalletAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new OASISResult<IWallet> { Result = w });

        var result = await _manager.UpdateAsync(wallet.Id, new WalletUpdateModel { Label = "New" });

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

        _provider.Setup(p => p.LoadWalletAsync(current.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IWallet> { Result = current });
        _provider.Setup(p => p.LoadAllWalletsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = new[] { prev } });
        _provider.Setup(p => p.SaveWalletAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new OASISResult<IWallet> { Result = w });

        var result = await _manager.SetDefaultAsync(avatarId, current.Id);

        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        current.IsDefault.Should().BeTrue();
        _provider.Verify(p => p.SaveWalletAsync(It.Is<IWallet>(w => w.Id == prev.Id && !w.IsDefault), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetDefaultAsync_WrongAvatar_ReturnsError()
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid(), ChainType = "Solana", Address = "addr" };
        _provider.Setup(p => p.LoadWalletAsync(wallet.Id, It.IsAny<CancellationToken>()))
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

        _provider.Setup(p => p.LoadWalletAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IWallet> { Result = wallet });
        _provider.Setup(p => p.LoadAllHolonsAsync(null, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new[] { nft } });

        var result = await _manager.GetPortfolioAsync(wallet.Id);

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

        _provider.Setup(p => p.LoadAllWalletsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = new[] { w1, w2 } });

        var result = await _manager.QueryAsync(new WalletQueryRequest { AvatarId = w1.AvatarId, IsDefault = true });

        result.IsError.Should().BeFalse();
        result.Result.Should().ContainSingle(w => w.Id == w1.Id);
    }
}
