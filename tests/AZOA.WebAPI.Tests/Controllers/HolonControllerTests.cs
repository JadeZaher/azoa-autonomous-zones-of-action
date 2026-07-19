using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Controllers;

public class HolonControllerTests
{
    private readonly Mock<IHolonManager> _holonManager;
    private readonly Mock<IBlockchainOperationManager> _blockchainManager;
    private readonly HolonController _controller;

    public HolonControllerTests()
    {
        _holonManager = new Mock<IHolonManager>();
        _blockchainManager = new Mock<IBlockchainOperationManager>();
        _controller = new HolonController(_holonManager.Object, _blockchainManager.Object);
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
    public async Task Get_Existing_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _holonManager.Setup(m => m.GetAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IHolon> { Result = new Holon() });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_NonExisting_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _holonManager.Setup(m => m.GetAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IHolon> { IsError = true, Result = null });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Query_ReturnsOk()
    {
        _holonManager.Setup(m => m.QueryAsync(It.IsAny<HolonQueryRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });

        var result = await _controller.Query(new HolonQueryRequest(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Create_Success_ReturnsOk()
    {
        var model = new HolonCreateModel { Name = "Test" };
        _holonManager.Setup(m => m.CreateAsync(model, It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IHolon> { Result = new Holon() });

        var result = await _controller.Create(model, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Create_Error_ReturnsBadRequest()
    {
        var model = new HolonCreateModel();
        _holonManager.Setup(m => m.CreateAsync(model, It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IHolon> { IsError = true });

        var result = await _controller.Create(model, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _holonManager.Setup(m => m.UpdateAsync(id, It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IHolon> { Result = new Holon() });

        var result = await _controller.Update(id, new HolonUpdateModel(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Update_Error_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        _holonManager.Setup(m => m.UpdateAsync(id, It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IHolon> { IsError = true });

        var result = await _controller.Update(id, new HolonUpdateModel(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Delete_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _holonManager.Setup(m => m.DeleteAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<bool> { Result = true });

        var result = await _controller.Delete(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Delete_Failure_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _holonManager.Setup(m => m.DeleteAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<bool> { IsError = true, Result = false });

        var result = await _controller.Delete(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Interact_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _holonManager.Setup(m => m.InteractAsync(id, It.IsAny<HolonInteractionRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IHolon> { Result = new Holon() });

        var result = await _controller.Interact(id, new HolonInteractionRequest(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Interact_Error_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        _holonManager.Setup(m => m.InteractAsync(id, It.IsAny<HolonInteractionRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IHolon> { IsError = true });

        var result = await _controller.Interact(id, new HolonInteractionRequest(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Mint_Success_ReturnsOk()
    {
        _blockchainManager.Setup(m => m.BuildAndExecuteAsync(It.IsAny<Func<BlockchainOperationBuilder, IBlockchainOperation>>(), It.IsAny<AZOARequest?>()))
                          .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _controller.Mint(Guid.NewGuid(), new MintRequest { WalletId = Guid.NewGuid(), TokenUri = "uri", Amount = 1, AssetType = "NFT" }, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Mint_Success_ProjectsOnlyTheSafeOperationAllowlist()
    {
        var operation = new BlockchainOperation
        {
            IdempotencyKey = new string('b', 64),
            InitiatorApiKeyId = Guid.NewGuid(),
            Parameters = new Dictionary<string, string>
            {
                ["TxHash"] = "public-transaction",
                ["IdempotencyKey"] = "holon:private-api-key:purchase-a",
                ["IdempotencyResultPayload"] = "{\"internal\":true}",
            },
        };
        _blockchainManager.Setup(m => m.BuildAndExecuteAsync(
                It.IsAny<Func<BlockchainOperationBuilder, IBlockchainOperation>>(),
                It.IsAny<AZOARequest?>()))
            .ReturnsAsync(AZOAResult<IBlockchainOperation>.Success(operation));

        var action = await _controller.Mint(
            Guid.NewGuid(),
            new MintRequest { WalletId = Guid.NewGuid(), TokenUri = "uri", Amount = 1, AssetType = "NFT" },
            null);

        var json = System.Text.Json.JsonSerializer.Serialize(((OkObjectResult)action.Result!).Value);
        json.Should().Contain("public-transaction")
            .And.NotContain("holon:private-api-key")
            .And.NotContain("IdempotencyResultPayload")
            .And.NotContain("InitiatorApiKeyId");
    }

    [Fact]
    public async Task Mint_Error_ReturnsBadRequest()
    {
        _blockchainManager.Setup(m => m.BuildAndExecuteAsync(It.IsAny<Func<BlockchainOperationBuilder, IBlockchainOperation>>(), It.IsAny<AZOARequest?>()))
                          .ReturnsAsync(new AZOAResult<IBlockchainOperation> { IsError = true });

        var result = await _controller.Mint(Guid.NewGuid(), new MintRequest(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Exchange_Success_ReturnsOk()
    {
        _blockchainManager.Setup(m => m.BuildAndExecuteAsync(It.IsAny<Func<BlockchainOperationBuilder, IBlockchainOperation>>(), It.IsAny<AZOARequest?>()))
                          .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _controller.Exchange(Guid.NewGuid(), new ExchangeRequest { WalletId = Guid.NewGuid(), TargetHolonId = Guid.NewGuid(), ExchangeRate = "1:1" }, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Exchange_Error_ReturnsBadRequest()
    {
        _blockchainManager.Setup(m => m.BuildAndExecuteAsync(It.IsAny<Func<BlockchainOperationBuilder, IBlockchainOperation>>(), It.IsAny<AZOARequest?>()))
                          .ReturnsAsync(new AZOAResult<IBlockchainOperation> { IsError = true });

        var result = await _controller.Exchange(Guid.NewGuid(), new ExchangeRequest(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ─── Holarchy traversal tests ───

    [Fact]
    public async Task GetChildren_ReturnsOk()
    {
        _holonManager.Setup(m => m.GetChildrenAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { new Holon() } });

        var result = await _controller.GetChildren(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetPeers_Success_ReturnsOk()
    {
        _holonManager.Setup(m => m.GetPeersAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { new Holon() } });

        var result = await _controller.GetPeers(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetPeers_Error_ReturnsNotFound()
    {
        _holonManager.Setup(m => m.GetPeersAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { IsError = true });

        var result = await _controller.GetPeers(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetAncestors_Success_ReturnsOk()
    {
        _holonManager.Setup(m => m.GetAncestorsAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { new Holon() } });

        var result = await _controller.GetAncestors(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetAncestors_Error_ReturnsNotFound()
    {
        _holonManager.Setup(m => m.GetAncestorsAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { IsError = true });

        var result = await _controller.GetAncestors(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetDescendants_Success_ReturnsOk()
    {
        _holonManager.Setup(m => m.GetDescendantsAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { new Holon() } });

        var result = await _controller.GetDescendants(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetDescendants_Error_ReturnsNotFound()
    {
        _holonManager.Setup(m => m.GetDescendantsAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { IsError = true });

        var result = await _controller.GetDescendants(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    // ─── Holonic functionality tests ───

    [Fact]
    public async Task Propagate_Success_ReturnsOk()
    {
        _holonManager.Setup(m => m.PropagateAsync(It.IsAny<Guid>(), It.IsAny<HolonPropagateRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<int> { Result = 3 });

        var result = await _controller.Propagate(Guid.NewGuid(), new HolonPropagateRequest(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Propagate_Error_ReturnsBadRequest()
    {
        _holonManager.Setup(m => m.PropagateAsync(It.IsAny<Guid>(), It.IsAny<HolonPropagateRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<int> { IsError = true });

        var result = await _controller.Propagate(Guid.NewGuid(), new HolonPropagateRequest(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Compose_Success_ReturnsOk()
    {
        _holonManager.Setup(m => m.ComposeAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<HolonComposition> { Result = new HolonComposition() });

        var result = await _controller.Compose(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Compose_NotFound_ReturnsNotFound()
    {
        _holonManager.Setup(m => m.ComposeAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<HolonComposition> { IsError = true });

        var result = await _controller.Compose(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Clone_Success_ReturnsOk()
    {
        _holonManager.Setup(m => m.CloneAsync(It.IsAny<Guid>(), It.IsAny<HolonCloneRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IHolon> { Result = new Holon() });

        var result = await _controller.Clone(Guid.NewGuid(), new HolonCloneRequest(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Clone_Error_ReturnsBadRequest()
    {
        _holonManager.Setup(m => m.CloneAsync(It.IsAny<Guid>(), It.IsAny<HolonCloneRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<IHolon> { IsError = true });

        var result = await _controller.Clone(Guid.NewGuid(), new HolonCloneRequest(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Clone_NoAuth_ReturnsUnauthorized()
    {
        var noAuthController = new HolonController(_holonManager.Object, _blockchainManager.Object);
        noAuthController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await noAuthController.Clone(Guid.NewGuid(), new HolonCloneRequest(), null);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task MoveSubtree_Success_ReturnsOk()
    {
        _holonManager.Setup(m => m.MoveSubtreeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<bool> { Result = true });

        var result = await _controller.MoveSubtree(Guid.NewGuid(), new MoveSubtreeRequest { NewParentId = Guid.NewGuid() }, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task MoveSubtree_Error_ReturnsBadRequest()
    {
        _holonManager.Setup(m => m.MoveSubtreeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                     .ReturnsAsync(new AZOAResult<bool> { IsError = true });

        var result = await _controller.MoveSubtree(Guid.NewGuid(), new MoveSubtreeRequest { NewParentId = Guid.NewGuid() }, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }
}
