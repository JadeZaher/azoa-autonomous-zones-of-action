using Microsoft.EntityFrameworkCore;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;
using Xunit;

namespace OASIS.WebAPI.Tests.Managers;

public class AvatarNFTServiceTests
{
    private readonly Mock<ProviderContext> _mockProviderContext;
    private readonly Mock<IBlockchainOperationManager> _mockBlockchainOperationManager;
    private readonly AvatarNFTService _avatarNFTService;
    private readonly Guid _testAvatarId = Guid.NewGuid();
    private readonly Guid _testWalletId = Guid.NewGuid();
    private readonly Guid _testHolonId = Guid.NewGuid();

    public AvatarNFTServiceTests()
    {
        _mockProviderContext = new Mock<ProviderContext>();
        _mockBlockchainOperationManager = new Mock<IBlockchainOperationManager>();
        _avatarNFTService = new AvatarNFTService(_mockProviderContext.Object, _mockBlockchainOperationManager.Object);
    }

    [Fact]
    public async Task MintAvatarNFTAsync_WithValidModel_ShouldMintNFT()
    {
        // Arrange
        var mintModel = new AvatarNFTMintModel
        {
            ChainType = "Solana",
            NFTContractAddress = "11111111111111111111111111111111",
            TokenStandard = "ERC721",
            MetadataURI = "https://api.example.com/metadata/123",
            Name = "Test Avatar",
            Description = "Test description",
            IsSoulbound = false,
            IsTransferable = true
        };

        var mockAvatar = new Avatar { Id = _testAvatarId };
        var mockWallet = new Wallet { Id = _testWalletId, Address = "test_wallet_address", ChainType = "Solana" };
        
        _mockProviderContext.Setup(x => x.Activate(It.IsAny<OASISRequest>()))
            .Returns(new OASISResult<bool> { Result = true });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadAvatarAsync(_testAvatarId))
            .ReturnsAsync(new OASISResult<IAvatar> { Result = mockAvatar });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadWalletsByAvatarAsync(_testAvatarId))
            .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = new List<IWallet> { mockWallet } });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.SaveAvatarNFTAsync(It.IsAny<AvatarNFT>()))
            .ReturnsAsync(new OASISResult<IAvatarNFT> { Result = new AvatarNFT { Id = Guid.NewGuid() } });
        
        _mockBlockchainOperationManager.Setup(x => x.MintNFTAsync(It.IsAny<STARDappGenerationRequest>()))
            .ReturnsAsync(new OASISResult<bool> { Result = true });

        // Act
        var result = await _avatarNFTService.MintAvatarNFTAsync(_testAvatarId, mintModel);

        // Assert
        Assert.False(result.IsError);
        Assert.NotNull(result.Result);
        Assert.Equal(_testAvatarId, result.Result.AvatarId);
        Assert.Equal("Solana", result.Result.ChainType);
    }

    [Fact]
    public async Task MintAvatarNFTAsync_WithInvalidAvatar_ShouldReturnError()
    {
        // Arrange
        var mintModel = new AvatarNFTMintModel();
        
        _mockProviderContext.Setup(x => x.Activate(It.IsAny<OASISRequest>()))
            .Returns(new OASISResult<bool> { Result = true });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadAvatarAsync(_testAvatarId))
            .ReturnsAsync(new OASISResult<IAvatar> { IsError = true, Message = "Avatar not found" });

        // Act
        var result = await _avatarNFTService.MintAvatarNFTAsync(_testAvatarId, mintModel);

        // Assert
        Assert.True(result.IsError);
        Assert.Equal("Avatar not found.", result.Message);
    }

    [Fact]
    public async Task GetAvatarNFTAsync_WithValidId_ShouldReturnNFT()
    {
        // Arrange
        var expectedNFT = new AvatarNFT { Id = Guid.NewGuid(), Name = "Test NFT" };
        
        _mockProviderContext.Setup(x => x.Activate(It.IsAny<OASISRequest>()))
            .Returns(new OASISResult<bool> { Result = true });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadAvatarNFTAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new OASISResult<IAvatarNFT> { Result = expectedNFT });

        // Act
        var result = await _avatarNFTService.GetAvatarNFTAsync(expectedNFT.Id);

        // Assert
        Assert.False(result.IsError);
        Assert.NotNull(result.Result);
        Assert.Equal(expectedNFT.Id, result.Result.Id);
        Assert.Equal("Test NFT", result.Result.Name);
    }

    [Fact]
    public async Task TransferAvatarNFTAsync_WithSoulboundNFT_ShouldReturnError()
    {
        // Arrange
        var soulboundNFT = new AvatarNFT { Id = Guid.NewGuid(), IsSoulbound = true };
        var recipientAddress = "recipient_address";
        
        _mockProviderContext.Setup(x => x.Activate(It.IsAny<OASISRequest>()))
            .Returns(new OASISResult<bool> { Result = true });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadAvatarNFTAsync(soulboundNFT.Id))
            .ReturnsAsync(new OASISResult<IAvatarNFT> { Result = soulboundNFT });

        // Act
        var result = await _avatarNFTService.TransferAvatarNFTAsync(soulboundNFT.Id, recipientAddress);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Cannot transfer soulbound NFT", result.Message);
    }

    [Fact]
    public async Task TransferAvatarNFTAsync_WithTransferableNFT_ShouldTransfer()
    {
        // Arrange
        var transferableNFT = new AvatarNFT { 
            Id = Guid.NewGuid(), 
            IsSoulbound = false, 
            IsTransferable = true,
            CurrentOwner = "current_owner"
        };
        var recipientAddress = "recipient_address";
        
        _mockProviderContext.Setup(x => x.Activate(It.IsAny<OASISRequest>()))
            .Returns(new OASISResult<bool> { Result = true });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadAvatarNFTAsync(transferableNFT.Id))
            .ReturnsAsync(new OASISResult<IAvatarNFT> { Result = transferableNFT });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.SaveAvatarNFTAsync(It.IsAny<AvatarNFT>()))
            .ReturnsAsync(new OASISResult<IAvatarNFT> { Result = transferableNFT });
        
        _mockBlockchainOperationManager.Setup(x => x.TransferNFTAsync(It.IsAny<STARDappGenerationRequest>()))
            .ReturnsAsync(new OASISResult<bool> { Result = true });

        // Act
        var result = await _avatarNFTService.TransferAvatarNFTAsync(transferableNFT.Id, recipientAddress);

        // Assert
        Assert.False(result.IsError);
        Assert.True(result.Result);
        Assert.Contains("transferred successfully", result.Message);
    }

    [Fact]
    public async Task BindHolonToAvatarNFTAsync_WithValidIds_ShouldCreateBinding()
    {
        // Arrange
        var bindingModel = new HolonNFTBindingModel
        {
            Role = "owner",
            PermissionLevel = "full",
            Permissions = new Dictionary<string, string> { { "read", "true" }, { "write", "true" } }
        };
        
        _mockProviderContext.Setup(x => x.Activate(It.IsAny<OASISRequest>()))
            .Returns(new OASISResult<bool> { Result = true });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.SaveHolonNFTBindingAsync(It.IsAny<HolonNFTBinding>()))
            .ReturnsAsync(new OASISResult<IHolonNFTBinding> { Result = new HolonNFTBinding { Id = Guid.NewGuid() } });

        // Act
        var result = await _avatarNFTService.BindHolonToAvatarNFTAsync(_testHolonId, Guid.NewGuid(), bindingModel);

        // Assert
        Assert.False(result.IsError);
        Assert.NotNull(result.Result);
        Assert.Equal("owner", result.Result.Role);
    }

    [Fact]
    public async Task VerifyHolonAccessAsync_WithValidPermissions_ShouldReturnTrue()
    {
        // Arrange
        var avatarNFTId = Guid.NewGuid();
        var holonBinding = new HolonNFTBinding
        {
            Id = Guid.NewGuid(),
            HolonId = _testHolonId,
            AvatarNFTId = avatarNFTId,
            Role = "owner",
            Permissions = new Dictionary<string, string> { { "execute", "true" } },
            IsActive = true
        };
        
        _mockProviderContext.Setup(x => x.Activate(It.IsAny<OASISRequest>()))
            .Returns(new OASISResult<bool> { Result = true });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadHolonNFTBindingsByAvatarNFTAsync(avatarNFTId))
            .ReturnsAsync(new OASISResult<IEnumerable<IHolonNFTBinding>> { Result = new List<IHolonNFTBinding> { holonBinding } });

        // Act
        var result = await _avatarNFTService.VerifyHolonAccessAsync(avatarNFTId, _testHolonId, "execute");

        // Assert
        Assert.False(result.IsError);
        Assert.True(result.Result);
        Assert.Contains("verified", result.Message);
    }

    [Fact]
    public async Task VerifyHolonAccessAsync_WithInvalidPermissions_ShouldReturnFalse()
    {
        // Arrange
        var avatarNFTId = Guid.NewGuid();
        var holonBinding = new HolonNFTBinding
        {
            Id = Guid.NewGuid(),
            HolonId = _testHolonId,
            AvatarNFTId = avatarNFTId,
            Role = "owner",
            Permissions = new Dictionary<string, string> { { "execute", "false" } },
            IsActive = true
        };
        
        _mockProviderContext.Setup(x => x.Activate(It.IsAny<OASISRequest>()))
            .Returns(new OASISResult<bool> { Result = true });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadHolonNFTBindingsByAvatarNFTAsync(avatarNFTId))
            .ReturnsAsync(new OASISResult<IEnumerable<IHolonNFTBinding>> { Result = new List<IHolonNFTBinding> { holonBinding } });

        // Act
        var result = await _avatarNFTService.VerifyHolonAccessAsync(avatarNFTId, _testHolonId, "execute");

        // Assert
        Assert.False(result.IsError);
        Assert.False(result.Result);
        Assert.Contains("denied", result.Message);
    }

    [Fact]
    public async Task GetAvatarNFTCompositeAsync_WithValidId_ShouldReturnComposite()
    {
        // Arrange
        var avatarNFTId = Guid.NewGuid();
        var avatarNFT = new AvatarNFT
        {
            Id = avatarNFTId,
            AvatarId = _testAvatarId,
            Name = "Test NFT",
            ChainType = "Solana"
        };
        
        var holonBinding = new HolonNFTBinding
        {
            Id = Guid.NewGuid(),
            HolonId = _testHolonId,
            AvatarNFTId = avatarNFTId,
            Role = "owner",
            Permissions = new Dictionary<string, string> { { "read", "true" } },
            IsActive = true
        };
        
        var walletBinding = new WalletNFTBinding
        {
            Id = Guid.NewGuid(),
            WalletId = _testWalletId,
            AvatarNFTId = avatarNFTId,
            BindingType = "primary",
            AccessPermissions = new Dictionary<string, string> { { "sign", "true" } },
            IsActive = true
        };
        
        _mockProviderContext.Setup(x => x.Activate(It.IsAny<OASISRequest>()))
            .Returns(new OASISResult<bool> { Result = true });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadAvatarNFTAsync(avatarNFTId))
            .ReturnsAsync(new OASISResult<IAvatarNFT> { Result = avatarNFT });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadHolonNFTBindingsByAvatarNFTAsync(avatarNFTId))
            .ReturnsAsync(new OASISResult<IEnumerable<IHolonNFTBinding>> { Result = new List<IHolonNFTBinding> { holonBinding } });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadWalletNFTBindingsByAvatarNFTAsync(avatarNFTId))
            .ReturnsAsync(new OASISResult<IEnumerable<IWalletNFTBinding>> { Result = new List<IWalletNFTBinding> { walletBinding } });

        // Act
        var result = await _avatarNFTService.GetAvatarNFTCompositeAsync(avatarNFTId);

        // Assert
        Assert.False(result.IsError);
        Assert.NotNull(result.Result);
        Assert.Equal(avatarNFTId, result.Result.AvatarNFTId);
        Assert.Equal(_testAvatarId, result.Result.AvatarId);
        Assert.Equal("Test NFT", result.Result.Name);
        Assert.Single(result.Result.HolonBindings);
        Assert.Single(result.Result.WalletBindings);
    }

    [Fact]
    public async Task VerifyAvatarNFTOwnershipAsync_WithValidOwnership_ShouldReturnTrue()
    {
        // Arrange
        var nft = new AvatarNFT
        {
            Id = Guid.NewGuid(),
            AvatarId = _testAvatarId,
            ChainType = "Solana",
            NFTContractAddress = "11111111111111111111111111111111",
            TokenId = "123",
            CurrentOwner = "test_address"
        };
        
        var wallet = new Wallet { Id = _testWalletId, Address = "test_address", ChainType = "Solana" };
        
        _mockProviderContext.Setup(x => x.Activate(It.IsAny<OASISRequest>()))
            .Returns(new OASISResult<bool> { Result = true });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadAvatarNFTByTokenIdAsync("Solana", nft.NFTContractAddress, nft.TokenId))
            .ReturnsAsync(new OASISResult<IAvatarNFT> { Result = nft });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadWalletsByAvatarAsync(_testAvatarId))
            .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = new List<IWallet> { wallet } });

        // Act
        var result = await _avatarNFTService.VerifyAvatarNFTOwnershipAsync(
            _testAvatarId, 
            "Solana", 
            nft.NFTContractAddress, 
            nft.TokenId
        );

        // Assert
        Assert.False(result.IsError);
        Assert.True(result.Result);
        Assert.Contains("verified", result.Message);
    }

    [Fact]
    public async Task BurnAvatarNFTAsync_WithValidNFT_ShouldBurn()
    {
        // Arrange
        var nft = new AvatarNFT { Id = Guid.NewGuid(), IsSoulbound = false };
        
        _mockProviderContext.Setup(x => x.Activate(It.IsAny<OASISRequest>()))
            .Returns(new OASISResult<bool> { Result = true });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadAvatarNFTAsync(nft.Id))
            .ReturnsAsync(new OASISResult<IAvatarNFT> { Result = nft });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.DeleteAvatarNFTAsync(nft.Id))
            .ReturnsAsync(new OASISResult<bool> { Result = true });
        
        _mockBlockchainOperationManager.Setup(x => x.BurnNFTAsync(It.IsAny<STARDappGenerationRequest>()))
            .ReturnsAsync(new OASISResult<bool> { Result = true });

        // Act
        var result = await _avatarNFTService.BurnAvatarNFTAsync(nft.Id);

        // Assert
        Assert.False(result.IsError);
        Assert.True(result.Result);
        Assert.Contains("Deleted", result.Message);
    }

    [Fact]
    public async Task BurnAvatarNFTAsync_WithSoulboundNFT_ShouldReturnError()
    {
        // Arrange
        var soulboundNFT = new AvatarNFT { Id = Guid.NewGuid(), IsSoulbound = true };
        
        _mockProviderContext.Setup(x => x.Activate(It.IsAny<OASISRequest>()))
            .Returns(new OASISResult<bool> { Result = true });
        
        _mockProviderContext.Setup(x => x.CurrentProvider.LoadAvatarNFTAsync(soulboundNFT.Id))
            .ReturnsAsync(new OASISResult<IAvatarNFT> { Result = soulboundNFT });

        // Act
        var result = await _avatarNFTService.BurnAvatarNFTAsync(soulboundNFT.Id);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Cannot burn soulbound NFT", result.Message);
    }
}