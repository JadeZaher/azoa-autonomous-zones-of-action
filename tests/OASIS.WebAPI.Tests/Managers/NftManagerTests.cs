using FluentAssertions;
using Moq;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests.Managers;

public class NftManagerTests
{
    private readonly Mock<IHolonStore> _holonStore;
    private readonly Mock<IBlockchainOperationStore> _blockchainOperationStore;
    private readonly Mock<IKycGateService> _kycGate;
    private readonly NftManager _manager;

    public NftManagerTests()
    {
        _holonStore = new Mock<IHolonStore>();
        _blockchainOperationStore = new Mock<IBlockchainOperationStore>();
        _kycGate = new Mock<IKycGateService>();
        // value-path-wiring H3: MintAsync now gates on KYC at the choke point.
        // Default to approved so the existing mint-path tests exercise the same
        // behaviour; the dedicated H3 test overrides this to assert fail-closed.
        _kycGate.Setup(k => k.RequireVerifiedAsync(It.IsAny<Guid>()))
                .ReturnsAsync(new OASISResult<bool> { Result = true, Message = "Success" });
        _manager = new NftManager(_holonStore.Object, _blockchainOperationStore.Object, _kycGate.Object);
    }

    private static INft CreateNftMock(Guid id, string name, string assetType = "NFT", Guid? avatarId = null, string? chainId = null) =>
        Moq.Mock.Of<INft>(n => n.Id == id && n.Name == name && n.AssetType == assetType && n.AvatarId == avatarId && n.ChainId == chainId && n.CreatedDate == DateTime.UtcNow);

    [Fact]
    public async Task GetAsync_NftFound_ReturnsSuccess()
    {
        var id = Guid.NewGuid();
        var nft = CreateNftMock(id, "NFT1");
        _holonStore.Setup(p => p.GetByIdAsync(id, default))
            .ReturnsAsync(new OASISResult<IHolon> { Result = nft });

        var result = await _manager.GetAsync(id);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAsync_NotAnNft_ReturnsError()
    {
        var id = Guid.NewGuid();
        var holon = CreateNftMock(id, "Regular", "Document");
        _holonStore.Setup(p => p.GetByIdAsync(id, default))
            .ReturnsAsync(new OASISResult<IHolon> { Result = holon });

        var result = await _manager.GetAsync(id);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not an NFT");
    }

    [Fact]
    public async Task GetAsync_NotFound_ReturnsError()
    {
        _holonStore.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync(new OASISResult<IHolon> { IsError = true, Message = "Not found" });

        var result = await _manager.GetAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task QueryAsync_FiltersByOwnerAvatarId()
    {
        var avatarId = Guid.NewGuid();
        var nft1 = CreateNftMock(Guid.NewGuid(), "A", "NFT", avatarId);
        var nft2 = CreateNftMock(Guid.NewGuid(), "B", "NFT", Guid.NewGuid());
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new List<IHolon> { nft1, nft2 } });

        var result = await _manager.QueryAsync(new NftQueryRequest { OwnerAvatarId = avatarId });

        result.Result.Should().ContainSingle();
    }

    [Fact]
    public async Task QueryAsync_FiltersByChainId()
    {
        var nft1 = CreateNftMock(Guid.NewGuid(), "A", "NFT", chainId: "solana");
        var nft2 = CreateNftMock(Guid.NewGuid(), "B", "NFT", chainId: "algorand");
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new List<IHolon> { nft1, nft2 } });

        var result = await _manager.QueryAsync(new NftQueryRequest { ChainId = "solana" });

        result.Result.Should().ContainSingle();
    }

    [Fact]
    public async Task QueryAsync_FiltersNonNfts()
    {
        var nft = CreateNftMock(Guid.NewGuid(), "NFT", "NFT");
        var holon = CreateNftMock(Guid.NewGuid(), "Doc", "Document");
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new List<IHolon> { nft, holon } });

        var result = await _manager.QueryAsync(new NftQueryRequest());

        result.Result.Should().ContainSingle();
    }

    // ── value-path-wiring H3: KYC gate at the single choke point ──────────────

    [Fact]
    public async Task MintAsync_UnverifiedAvatar_RejectedWithNoHolonAndNoOperationSideEffect()
    {
        var avatarId = Guid.NewGuid();
        // KYC gate closed: the latest submission is not APPROVED.
        _kycGate.Setup(k => k.RequireVerifiedAsync(avatarId))
                .ReturnsAsync(new OASISResult<bool>
                {
                    IsError = true,
                    Result = false,
                    Message = $"{KycAuthorizationError.Forbidden}{KycAuthorizationError.VerificationRequiredMessage}"
                });

        var request = new NftMintRequest { Name = "NFT", Description = "D", ChainId = "algorand", WalletId = Guid.NewGuid() };
        var result = await _manager.MintAsync(request, avatarId);

        result.IsError.Should().BeTrue("an unverified avatar must be rejected at the NftManager choke point");
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden,
            "the KYC_FORBIDDEN: prefix must be preserved so the controller maps it to 403");

        // No side effect before the gate: no Holon upsert and no BlockchainOperation.
        _holonStore.Verify(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
        _blockchainOperationStore.Verify(
            p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MintAsync_CreatesHolonWithNftAssetType()
    {
        var avatarId = Guid.NewGuid();
        var request = new NftMintRequest { Name = "MyNFT", Description = "Desc", ChainId = "solana", WalletId = Guid.NewGuid() };
        _holonStore.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), default))
            .ReturnsAsync(new OASISResult<IHolon> { Result = new Holon { Id = Guid.NewGuid(), AssetType = "NFT" } });
        _blockchainOperationStore.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), default))
            .ReturnsAsync(new OASISResult<IBlockchainOperation> { Result = new BlockchainOperation { OperationType = "Mint" } });

        var result = await _manager.MintAsync(request, avatarId);

        result.IsError.Should().BeFalse();
        _holonStore.Verify(p => p.UpsertAsync(It.Is<IHolon>(h => h.AssetType == "NFT" && h.AvatarId == avatarId), default), Times.Once);
    }

    [Fact]
    public async Task TransferAsync_UpdatesOwnership()
    {
        var avatarId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var nftId = Guid.NewGuid();
        var nft = CreateNftMock(nftId, "NFT", "NFT", avatarId);
        _holonStore.Setup(p => p.GetByIdAsync(nftId, default))
            .ReturnsAsync(new OASISResult<IHolon> { Result = nft });
        _holonStore.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), default))
            .ReturnsAsync(new OASISResult<IHolon> { Result = nft });
        _blockchainOperationStore.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), default))
            .ReturnsAsync(new OASISResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _manager.TransferAsync(nftId, new NftTransferRequest { TargetAvatarId = targetId, WalletId = Guid.NewGuid() }, avatarId);

        result.IsError.Should().BeFalse();
        nft.AvatarId.Should().Be(targetId);
    }

    [Fact]
    public async Task TransferAsync_WrongOwner_ReturnsError()
    {
        var avatarId = Guid.NewGuid();
        var nft = CreateNftMock(Guid.NewGuid(), "NFT", "NFT", Guid.NewGuid());
        _holonStore.Setup(p => p.GetByIdAsync(nft.Id, default))
            .ReturnsAsync(new OASISResult<IHolon> { Result = nft });

        var result = await _manager.TransferAsync(nft.Id, new NftTransferRequest(), avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("do not own");
    }

    [Fact]
    public async Task BurnAsync_DeactivatesHolon()
    {
        var avatarId = Guid.NewGuid();
        var nft = CreateNftMock(Guid.NewGuid(), "NFT", "NFT", avatarId);
        nft.IsActive = true;
        _holonStore.Setup(p => p.GetByIdAsync(nft.Id, default))
            .ReturnsAsync(new OASISResult<IHolon> { Result = nft });
        _holonStore.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), default))
            .ReturnsAsync(new OASISResult<IHolon> { Result = nft });
        _blockchainOperationStore.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), default))
            .ReturnsAsync(new OASISResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _manager.BurnAsync(nft.Id, Guid.NewGuid(), avatarId);

        result.IsError.Should().BeFalse();
        nft.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsErc721Shape()
    {
        var nft = Moq.Mock.Of<INft>(n => n.Id == Guid.NewGuid() && n.Name == "MyNFT" && n.AssetType == "NFT" && n.CreatedDate == DateTime.UtcNow);
        var mock = Moq.Mock.Get(nft);
        mock.SetupGet(n => n.Description).Returns("A cool NFT");
        mock.SetupGet(n => n.Metadata).Returns(new Dictionary<string, string>
        {
            ["image"] = "https://example.com/img.png",
            ["external_url"] = "https://example.com"
        });
        _holonStore.Setup(p => p.GetByIdAsync(nft.Id, default))
            .ReturnsAsync(new OASISResult<IHolon> { Result = nft });

        var result = await _manager.GetMetadataAsync(nft.Id);

        result.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be("MyNFT");
        result.Result!.Image.Should().Be("https://example.com/img.png");
        result.Result!.ExternalUrl.Should().Be("https://example.com");
    }
}
