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

public class WalletControllerTests
{
    private readonly Mock<IWalletManager> _walletManager;
    private readonly WalletController _controller;

    public WalletControllerTests()
    {
        _walletManager = new Mock<IWalletManager>();
        _controller = new WalletController(_walletManager.Object);
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
        _walletManager.Setup(m => m.GetAsync(id, It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                      .ReturnsAsync(new AZOAResult<IWallet> { Result = new Wallet() });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_NonExisting_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _walletManager.Setup(m => m.GetAsync(id, It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                      .ReturnsAsync(new AZOAResult<IWallet> { IsError = true, Result = null });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Query_ReturnsOk()
    {
        _walletManager.Setup(m => m.QueryAsync(It.IsAny<WalletQueryRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                      .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = Array.Empty<IWallet>() });

        var result = await _controller.Query(new WalletQueryRequest(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Create_Success_ReturnsOk()
    {
        var model = new WalletCreateModel { ChainType = "Solana", Address = "addr" };
        _walletManager.Setup(m => m.CreateAsync(model, It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                      .ReturnsAsync(new AZOAResult<IWallet> { Result = new Wallet() });

        var result = await _controller.Create(model, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Create_Error_ReturnsBadRequest()
    {
        var model = new WalletCreateModel();
        _walletManager.Setup(m => m.CreateAsync(model, It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                      .ReturnsAsync(new AZOAResult<IWallet> { IsError = true });

        var result = await _controller.Create(model, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_NoAuth_ReturnsUnauthorized()
    {
        var noAuthController = new WalletController(_walletManager.Object);
        noAuthController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await noAuthController.Create(new WalletCreateModel(), null);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Update_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _walletManager.Setup(m => m.UpdateAsync(id, It.IsAny<WalletUpdateModel>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                      .ReturnsAsync(new AZOAResult<IWallet> { Result = new Wallet() });

        var result = await _controller.Update(id, new WalletUpdateModel(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Update_Error_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        _walletManager.Setup(m => m.UpdateAsync(id, It.IsAny<WalletUpdateModel>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                      .ReturnsAsync(new AZOAResult<IWallet> { IsError = true });

        var result = await _controller.Update(id, new WalletUpdateModel(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Delete_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _walletManager.Setup(m => m.DeleteAsync(id, It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                      .ReturnsAsync(new AZOAResult<bool> { Result = true });

        var result = await _controller.Delete(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Delete_Failure_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _walletManager.Setup(m => m.DeleteAsync(id, It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                      .ReturnsAsync(new AZOAResult<bool> { IsError = true, Result = false });

        var result = await _controller.Delete(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task SetDefault_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _walletManager.Setup(m => m.SetDefaultAsync(It.IsAny<Guid>(), id, It.IsAny<AZOARequest?>()))
                      .ReturnsAsync(new AZOAResult<bool> { Result = true });

        var result = await _controller.SetDefault(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SetDefault_NoAuth_ReturnsUnauthorized()
    {
        var noAuthController = new WalletController(_walletManager.Object);
        noAuthController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await noAuthController.SetDefault(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task GetPortfolio_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _walletManager.Setup(m => m.GetPortfolioAsync(id, It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                      .ReturnsAsync(new AZOAResult<PortfolioResult> { Result = new PortfolioResult() });

        var result = await _controller.GetPortfolio(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetPortfolio_NotFound_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _walletManager.Setup(m => m.GetPortfolioAsync(id, It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                      .ReturnsAsync(new AZOAResult<PortfolioResult> { IsError = true });

        var result = await _controller.GetPortfolio(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task TopUp_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _walletManager.Setup(m => m.TopUpAsync(id, It.IsAny<decimal?>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<string?>()))
                      .ReturnsAsync(new AZOAResult<object> { Result = new { txHash = "abc" } });

        var result = await _controller.TopUp(id, new WalletTopUpRequest { Amount = 5m }, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task TopUp_Error_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        _walletManager.Setup(m => m.TopUpAsync(id, It.IsAny<decimal?>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<string?>()))
                      .ReturnsAsync(new AZOAResult<object> { IsError = true, Message = "Top-up (faucet) is disabled on mainnet." });

        var result = await _controller.TopUp(id, new WalletTopUpRequest(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task TopUp_NoAuth_ReturnsUnauthorized()
    {
        var noAuthController = new WalletController(_walletManager.Object);
        noAuthController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await noAuthController.TopUp(Guid.NewGuid(), new WalletTopUpRequest(), null);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }
}
