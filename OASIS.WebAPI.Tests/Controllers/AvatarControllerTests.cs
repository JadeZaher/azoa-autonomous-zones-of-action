using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using OASIS.WebAPI.Controllers;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests.Controllers;

public class AvatarControllerTests
{
    private readonly Mock<IAvatarManager> _manager;
    private readonly AvatarController _controller;

    public AvatarControllerTests()
    {
        _manager = new Mock<IAvatarManager>();
        _controller = new AvatarController(_manager.Object);
    }

    [Fact]
    public async Task Register_WithValidModel_ReturnsOk()
    {
        var model = new AvatarRegisterModel { Username = "neo", Email = "neo@test.com", Password = "pass" };
        _manager.Setup(m => m.RegisterAsync(model, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IAvatar> { Result = new Avatar { Username = "neo" } });

        var result = await _controller.Register(model, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Register_WithError_ReturnsBadRequest()
    {
        var model = new AvatarRegisterModel();
        _manager.Setup(m => m.RegisterAsync(model, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IAvatar> { IsError = true, Message = "Invalid" });

        var result = await _controller.Register(model, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsOk()
    {
        var model = new AvatarLoginModel { Email = "test@test.com", Password = "pass" };
        _manager.Setup(m => m.LoginAsync(model, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<string> { Result = "jwt_token" });

        var result = await _controller.Login(model, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ReturnsUnauthorized()
    {
        var model = new AvatarLoginModel();
        _manager.Setup(m => m.LoginAsync(model, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<string> { IsError = true });

        var result = await _controller.Login(model, null);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Get_Existing_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GetAsync(id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IAvatar> { Result = new Avatar() });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_NonExisting_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GetAsync(id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IAvatar> { IsError = true, Result = null });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetAll_ReturnsOk()
    {
        _manager.Setup(m => m.GetAllAsync(It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IEnumerable<IAvatar>> { Result = Array.Empty<IAvatar>() });

        var result = await _controller.GetAll(null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Update_WithError_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.UpdateAsync(id, It.IsAny<AvatarUpdateModel>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IAvatar> { IsError = true });

        var result = await _controller.Update(id, new AvatarUpdateModel(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.UpdateAsync(id, It.IsAny<AvatarUpdateModel>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IAvatar> { Result = new Avatar() });

        var result = await _controller.Update(id, new AvatarUpdateModel(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Delete_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeleteAsync(id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<bool> { Result = true });

        var result = await _controller.Delete(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Delete_Failure_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeleteAsync(id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<bool> { IsError = true, Result = false });

        var result = await _controller.Delete(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task AddWallet_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.AddWalletAsync(id, It.IsAny<Wallet>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IWallet> { Result = new Wallet() });

        var result = await _controller.AddWallet(id, new Wallet(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task AddWallet_Error_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.AddWalletAsync(id, It.IsAny<Wallet>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IWallet> { IsError = true });

        var result = await _controller.AddWallet(id, new Wallet(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task RemoveWallet_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.RemoveWalletAsync(id, It.IsAny<Guid>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<bool> { Result = true });

        var result = await _controller.RemoveWallet(id, Guid.NewGuid(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task RemoveWallet_Failure_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.RemoveWalletAsync(id, It.IsAny<Guid>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<bool> { IsError = true, Result = false });

        var result = await _controller.RemoveWallet(id, Guid.NewGuid(), null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetWallets_ReturnsOk()
    {
        _manager.Setup(m => m.GetWalletsAsync(It.IsAny<Guid>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = Array.Empty<IWallet>() });

        var result = await _controller.GetWallets(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }
}
