using System.Globalization;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Services.Signing;
using SurrealForge.Client;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests.Controllers;

public class NftControllerTests
{
    private readonly Mock<INftManager> _nftManager;
    private readonly Mock<IFungibleTokenManager> _fungibleTokenManager;
    private readonly NftController _controller;
    private readonly Guid _actor = Guid.NewGuid();

    public NftControllerTests()
    {
        _nftManager = new Mock<INftManager>();
        _fungibleTokenManager = new Mock<IFungibleTokenManager>();
        var users = new Mock<IUserStore>();
        users.Setup(store => store.GetByIdAsync(_actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IUser>.Success(Mock.Of<IUser>(user => user.IsActive)));
        var signing = new SigningAdmissionAuthorityFactory(users.Object, Mock.Of<IWalletStore>(), Mock.Of<ISurrealExecutor>());
        _controller = new NftController(_nftManager.Object, _fungibleTokenManager.Object, signing);
        var now = DateTimeOffset.UtcNow;
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, _actor.ToString()),
                    new Claim(AZOA.WebAPI.Core.AzoaClaims.TokenUse, AZOA.WebAPI.Core.AzoaClaims.TokenUseLogin),
                    new Claim(AZOA.WebAPI.Core.AzoaClaims.AuthTime, now.AddMinutes(-1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
                    new Claim("exp", now.AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
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
        _nftManager.Setup(m => m.GetAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync(new AZOAResult<INft> { Result = mockNft.Object });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_NotFound_ReturnsNotFound()
    {
        _nftManager.Setup(m => m.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync(new AZOAResult<INft> { IsError = true });

        var result = await _controller.Get(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Query_ReturnsOk()
    {
        _nftManager.Setup(m => m.QueryAsync(It.IsAny<NftQueryRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<INft>> { Result = Enumerable.Empty<INft>() });

        var result = await _controller.Query(new NftQueryRequest(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Mint_Success_ReturnsOk()
    {
        var req = new NftMintRequest { Name = "NFT", Description = "D", ChainId = "sol", WalletId = Guid.NewGuid() };
        _nftManager.Setup(m => m.MintAsync(req, It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _controller.Mint(req, null);

        result.Result.Should().BeOfType<OkObjectResult>();
        _nftManager.Verify(m => m.MintAsync(req, _actor, It.Is<AZOARequest?>(forwarded => forwarded!.AuthenticationWitness!.ActorUserId == _actor
            && forwarded.AuthenticationWitness.Kind == "Jwt"), It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task Mint_Success_ProjectsOnlyTheSafeOperationAllowlist()
    {
        var request = new NftMintRequest { Name = "NFT", ChainId = "sol", WalletId = Guid.NewGuid() };
        var operation = new BlockchainOperation
        {
            IdempotencyKey = new string('a', 64),
            InitiatorApiKeyId = Guid.NewGuid(),
            Parameters = new Dictionary<string, string>
            {
                ["TxHash"] = "public-transaction",
                ["IdempotencyKey"] = "nft:private-api-key:purchase-a",
                ["IdempotencyResultPayload"] = "{\"internal\":true}",
            },
        };
        _nftManager.Setup(m => m.MintAsync(request, It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(AZOAResult<IBlockchainOperation>.Success(operation));

        var action = await _controller.Mint(request, null);

        var json = System.Text.Json.JsonSerializer.Serialize(((OkObjectResult)action.Result!).Value);
        json.Should().Contain("public-transaction")
            .And.NotContain("nft:private-api-key")
            .And.NotContain("IdempotencyResultPayload")
            .And.NotContain("InitiatorApiKeyId");
    }

    [Fact]
    public async Task Mint_NoAuth_ReturnsUnauthorized()
    {
        var noAuth = new NftController(_nftManager.Object, _fungibleTokenManager.Object);
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
        var req = new NftTransferRequest { TargetUserId = Guid.NewGuid(), WalletId = Guid.NewGuid() };
        _nftManager.Setup(m => m.TransferAsync(It.IsAny<Guid>(), req, It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _controller.Transfer(Guid.NewGuid(), req, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Burn_Success_ReturnsOk()
    {
        var req = new NftBurnRequest { WalletId = Guid.NewGuid() };
        _nftManager.Setup(m => m.BurnAsync(It.IsAny<Guid>(), req.WalletId, It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _controller.Burn(Guid.NewGuid(), req, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetMetadata_AnonymousCallerCannotBypassManagerAuthority()
    {
        _nftManager.Setup(m => m.GetMetadataAsync(It.IsAny<Guid>(), null, null))
            .ReturnsAsync(AZOAResult<NftMetadata>.Failure("Owner authority required"));

        var noAuth = new NftController(_nftManager.Object, _fungibleTokenManager.Object);
        noAuth.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await noAuth.GetMetadata(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }
}
