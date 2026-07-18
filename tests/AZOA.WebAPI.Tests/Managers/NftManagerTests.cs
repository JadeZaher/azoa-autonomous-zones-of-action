using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Managers;

public class NftManagerTests
{
    private readonly Mock<IHolonStore> _holonStore;
    private readonly Mock<IBlockchainOperationStore> _blockchainOperationStore;
    private readonly Mock<IKycGateService> _kycGate;
    private readonly Mock<INodeFeeScheduleManager> _nodeFees;
    private readonly Mock<INodeGovernanceGuard> _nodeGovernance;
    private readonly NftManager _manager;

    public NftManagerTests()
    {
        _holonStore = new Mock<IHolonStore>();
        _blockchainOperationStore = new Mock<IBlockchainOperationStore>();
        _kycGate = new Mock<IKycGateService>();
        _nodeFees = new Mock<INodeFeeScheduleManager>();
        _nodeGovernance = new Mock<INodeGovernanceGuard>();
        // value-path-wiring H3: MintAsync now gates on KYC at the choke point.
        // Default to approved so the existing mint-path tests exercise the same
        // behaviour; the dedicated H3 test overrides this to assert fail-closed.
        _kycGate.Setup(k => k.RequireVerifiedAsync(It.IsAny<Guid>()))
                .ReturnsAsync(new AZOAResult<bool> { Result = true, Message = "Success" });
        _nodeFees.Setup(f => f.GetScheduleAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeFeeScheduleResponse>
            {
                Result = new NodeFeeScheduleResponse(),
                Message = "Success",
            });
        _nodeGovernance.Setup(g => g.EnsureAllowedAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        _manager = new NftManager(
            _holonStore.Object,
            _blockchainOperationStore.Object,
            _kycGate.Object,
            _nodeFees.Object,
            _nodeGovernance.Object);
    }

    private static INft CreateNftMock(Guid id, string name, string assetType = "NFT", Guid? avatarId = null, string? chainId = null) =>
        Moq.Mock.Of<INft>(n => n.Id == id && n.Name == name && n.AssetType == assetType && n.AvatarId == avatarId && n.ChainId == chainId && n.CreatedDate == DateTime.UtcNow);

    [Fact]
    public async Task GetAsync_NftFound_ReturnsSuccess()
    {
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        var nft = CreateNftMock(id, "NFT1", avatarId: owner);
        _holonStore.Setup(p => p.GetByIdAsync(id, default))
            .ReturnsAsync(new AZOAResult<IHolon> { Result = nft });

        // Owner-or-public read scope: owner reads their own NFT.
        var result = await _manager.GetAsync(id, owner);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAsync_NotAnNft_ReturnsError()
    {
        var id = Guid.NewGuid();
        var holon = CreateNftMock(id, "Regular", "Document");
        _holonStore.Setup(p => p.GetByIdAsync(id, default))
            .ReturnsAsync(new AZOAResult<IHolon> { Result = holon });

        var result = await _manager.GetAsync(id);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not an NFT");
    }

    [Fact]
    public async Task GetAsync_NotFound_ReturnsError()
    {
        _holonStore.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync(new AZOAResult<IHolon> { IsError = true, Message = "Not found" });

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
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon> { nft1, nft2 } });

        // Caller is avatarId: owner-or-public scope already drops nft2 (private, other
        // owner); the OwnerAvatarId filter narrows within the readable set.
        var result = await _manager.QueryAsync(new NftQueryRequest { OwnerAvatarId = avatarId }, avatarId);

        result.Result.Should().ContainSingle();
    }

    [Fact]
    public async Task QueryAsync_FiltersByChainId()
    {
        var owner = Guid.NewGuid();
        var nft1 = CreateNftMock(Guid.NewGuid(), "A", "NFT", owner, chainId: "solana");
        var nft2 = CreateNftMock(Guid.NewGuid(), "B", "NFT", owner, chainId: "algorand");
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon> { nft1, nft2 } });

        var result = await _manager.QueryAsync(new NftQueryRequest { ChainId = "solana" }, owner);

        result.Result.Should().ContainSingle();
    }

    [Fact]
    public async Task QueryAsync_FiltersNonNfts()
    {
        var owner = Guid.NewGuid();
        var nft = CreateNftMock(Guid.NewGuid(), "NFT", "NFT", owner);
        var holon = CreateNftMock(Guid.NewGuid(), "Doc", "Document", owner);
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon> { nft, holon } });

        var result = await _manager.QueryAsync(new NftQueryRequest(), owner);

        result.Result.Should().ContainSingle();
    }

    // ── value-path-wiring H3: KYC gate at the single choke point ──────────────

    [Fact]
    public async Task MintAsync_UnverifiedAvatar_RejectedWithNoHolonAndNoOperationSideEffect()
    {
        var avatarId = Guid.NewGuid();
        // KYC gate closed: the latest submission is not APPROVED.
        _kycGate.Setup(k => k.RequireVerifiedAsync(avatarId))
                .ReturnsAsync(new AZOAResult<bool>
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
            .ReturnsAsync(new AZOAResult<IHolon> { Result = new Holon { Id = Guid.NewGuid(), AssetType = "NFT" } });
        _blockchainOperationStore.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), default))
            .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation { OperationType = "Mint" } });

        var result = await _manager.MintAsync(request, avatarId);

        result.IsError.Should().BeFalse();
        _holonStore.Verify(p => p.UpsertAsync(It.Is<IHolon>(h => h.AssetType == "NFT" && h.AvatarId == avatarId), default), Times.Once);
    }

    [Theory]
    [InlineData("1", 0)]
    [InlineData("0", 1)]
    public async Task MintAsync_NonzeroConfiguredFee_RejectsBeforeAnyMintSideEffect(
        string flatBaseUnits,
        long bps)
    {
        _nodeFees.Setup(f => f.GetScheduleAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeFeeScheduleResponse>
            {
                Result = new NodeFeeScheduleResponse
                {
                    Mint = new NodeFeeScheduleEntryResponse { FlatBaseUnits = flatBaseUnits, Bps = bps },
                },
            });

        var result = await _manager.MintAsync(new NftMintRequest { ChainId = "Algorand" }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("nonzero node Mint fee");
        VerifyNoMintSideEffect();
    }

    [Fact]
    public async Task MintAsync_UnavailableFeeSchedule_RejectsBeforeAnyMintSideEffect()
    {
        _nodeFees.Setup(f => f.GetScheduleAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeFeeScheduleResponse> { IsError = true, Message = "Store unavailable" });

        var result = await _manager.MintAsync(new NftMintRequest { ChainId = "Algorand" }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("fee schedule is unavailable");
        VerifyNoMintSideEffect();
    }

    [Fact]
    public async Task MintAsync_MalformedFeeSchedule_RejectsBeforeAnyMintSideEffect()
    {
        _nodeFees.Setup(f => f.GetScheduleAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeFeeScheduleResponse>
            {
                Result = new NodeFeeScheduleResponse
                {
                    Mint = new NodeFeeScheduleEntryResponse { FlatBaseUnits = "invalid" },
                },
            });

        var result = await _manager.MintAsync(new NftMintRequest { ChainId = "Algorand" }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("fee schedule is invalid");
        VerifyNoMintSideEffect();
    }

    [Fact]
    public async Task MintAsync_DisallowedGovernance_RejectsBeforeKycAndAnyMintSideEffect()
    {
        _nodeGovernance.Setup(g => g.EnsureAllowedAsync(
                "Algorand", "NFT", "nft:mint", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { IsError = true, Message = "Node governance disallows nft:mint." });

        var result = await _manager.MintAsync(new NftMintRequest { ChainId = "Algorand" }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        _kycGate.Verify(g => g.RequireVerifiedAsync(It.IsAny<Guid>()), Times.Never);
        VerifyNoMintSideEffect();
    }

    [Fact]
    public async Task TransferAsync_UpdatesOwnership()
    {
        var avatarId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var nftId = Guid.NewGuid();
        var nft = CreateNftMock(nftId, "NFT", "NFT", avatarId);
        _holonStore.Setup(p => p.GetByIdAsync(nftId, default))
            .ReturnsAsync(new AZOAResult<IHolon> { Result = nft });
        _holonStore.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), default))
            .ReturnsAsync(new AZOAResult<IHolon> { Result = nft });
        _blockchainOperationStore.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), default))
            .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _manager.TransferAsync(nftId, new NftTransferRequest { TargetAvatarId = targetId, WalletId = Guid.NewGuid() }, avatarId);

        result.IsError.Should().BeFalse();
        nft.AvatarId.Should().Be(targetId);
    }

    [Theory]
    [InlineData("1", 0)]
    [InlineData("0", 1)]
    public async Task TransferAsync_NonzeroConfiguredFee_RejectsBeforeAnyTransferSideEffect(
        string flatBaseUnits,
        long bps)
    {
        _nodeFees.Setup(f => f.GetScheduleAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeFeeScheduleResponse>
            {
                Result = new NodeFeeScheduleResponse
                {
                    Transfer = new NodeFeeScheduleEntryResponse
                    {
                        FlatBaseUnits = flatBaseUnits,
                        Bps = bps,
                    },
                },
                Message = "Success",
            });

        var result = await _manager.TransferAsync(
            Guid.NewGuid(),
            new NftTransferRequest { TargetAvatarId = Guid.NewGuid(), WalletId = Guid.NewGuid() },
            Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("nonzero node Transfer fee");
        _holonStore.Verify(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _holonStore.Verify(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
        _blockchainOperationStore.Verify(
            p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransferAsync_UnavailableFeeSchedule_RejectsBeforeAnyTransferSideEffect()
    {
        _nodeFees.Setup(f => f.GetScheduleAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeFeeScheduleResponse>
            {
                IsError = true,
                Message = "Surreal unavailable",
            });

        var result = await _manager.TransferAsync(
            Guid.NewGuid(),
            new NftTransferRequest { TargetAvatarId = Guid.NewGuid(), WalletId = Guid.NewGuid() },
            Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("fee schedule is unavailable");
        _holonStore.Verify(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _holonStore.Verify(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
        _blockchainOperationStore.Verify(
            p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransferAsync_MalformedFeeSchedule_RejectsBeforeAnyTransferSideEffect()
    {
        _nodeFees.Setup(f => f.GetScheduleAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeFeeScheduleResponse>
            {
                Result = new NodeFeeScheduleResponse
                {
                    Transfer = new NodeFeeScheduleEntryResponse { FlatBaseUnits = "invalid" },
                },
                Message = "Success",
            });

        var result = await _manager.TransferAsync(
            Guid.NewGuid(),
            new NftTransferRequest { TargetAvatarId = Guid.NewGuid(), WalletId = Guid.NewGuid() },
            Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("fee schedule is invalid");
        _holonStore.Verify(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _holonStore.Verify(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
        _blockchainOperationStore.Verify(
            p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransferAsync_WrongOwner_ReturnsError()
    {
        var avatarId = Guid.NewGuid();
        var nft = CreateNftMock(Guid.NewGuid(), "NFT", "NFT", Guid.NewGuid());
        _holonStore.Setup(p => p.GetByIdAsync(nft.Id, default))
            .ReturnsAsync(new AZOAResult<IHolon> { Result = nft });

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
            .ReturnsAsync(new AZOAResult<IHolon> { Result = nft });
        _holonStore.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), default))
            .ReturnsAsync(new AZOAResult<IHolon> { Result = nft });
        _blockchainOperationStore.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), default))
            .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

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
            .ReturnsAsync(new AZOAResult<IHolon> { Result = nft });

        var result = await _manager.GetMetadataAsync(nft.Id);

        result.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be("MyNFT");
        result.Result!.Image.Should().Be("https://example.com/img.png");
        result.Result!.ExternalUrl.Should().Be("https://example.com");
    }

    private void VerifyNoMintSideEffect()
    {
        _holonStore.Verify(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
        _blockchainOperationStore.Verify(
            p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
