using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Controllers;

public class NftControllerTests
{
    private readonly Mock<INftManager> _nftManager;
    private readonly NftController _controller;

    public NftControllerTests()
    {
        _nftManager = new Mock<INftManager>();
        _controller = new NftController(_nftManager.Object);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
                }, "TestScheme"))
            }
        };
    }

    [Fact]
    public async Task Get_ExistingNft_ReturnsOk()
    {
        var id = Guid.NewGuid();
        var mockNft = new Mock<INft>();
        mockNft.SetupGet(n => n.Id).Returns(id);
        mockNft.SetupGet(n => n.Name).Returns("NFT");
        mockNft.SetupGet(n => n.AssetType).Returns("NFT");
        mockNft.SetupGet(n => n.CreatedDate).Returns(DateTime.UtcNow);
        mockNft.SetupGet(n => n.Metadata).Returns(new Dictionary<string, string>());
        _nftManager.Setup(m => m.GetAsync(id, null))
            .ReturnsAsync(new AZOAResult<INft> { Result = mockNft.Object });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_NotFound_ReturnsNotFound()
    {
        _nftManager.Setup(m => m.GetAsync(It.IsAny<Guid>(), null))
            .ReturnsAsync(new AZOAResult<INft> { IsError = true });

        var result = await _controller.Get(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Query_ReturnsOk()
    {
        _nftManager.Setup(m => m.QueryAsync(It.IsAny<NftQueryRequest>(), null))
            .ReturnsAsync(new AZOAResult<IEnumerable<INft>> { Result = Enumerable.Empty<INft>() });

        var result = await _controller.Query(new NftQueryRequest(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Mint_Success_ReturnsOk()
    {
        var req = new NftMintRequest { Name = "NFT", Description = "D", ChainId = "sol", WalletId = Guid.NewGuid() };
        _nftManager.Setup(m => m.MintAsync(req, It.IsAny<Guid>(), null, It.IsAny<Guid?>()))
            .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _controller.Mint(req, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Mint_NoAuth_ReturnsUnauthorized()
    {
        var noAuth = new NftController(_nftManager.Object);
        noAuth.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await noAuth.Mint(new NftMintRequest(), null);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Transfer_Success_ReturnsOk()
    {
        var req = new NftTransferRequest { TargetAvatarId = Guid.NewGuid(), WalletId = Guid.NewGuid() };
        _nftManager.Setup(m => m.TransferAsync(It.IsAny<Guid>(), req, It.IsAny<Guid>(), null, It.IsAny<Guid?>()))
            .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _controller.Transfer(Guid.NewGuid(), req, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Burn_Success_ReturnsOk()
    {
        var req = new NftBurnRequest { WalletId = Guid.NewGuid() };
        _nftManager.Setup(m => m.BurnAsync(It.IsAny<Guid>(), req.WalletId, It.IsAny<Guid>(), null))
            .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _controller.Burn(Guid.NewGuid(), req, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetMetadata_AllowedAnonymously()
    {
        _nftManager.Setup(m => m.GetMetadataAsync(It.IsAny<Guid>(), null))
            .ReturnsAsync(new AZOAResult<NftMetadata> { Result = new NftMetadata() });

        var noAuth = new NftController(_nftManager.Object);
        noAuth.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await noAuth.GetMetadata(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }
}
