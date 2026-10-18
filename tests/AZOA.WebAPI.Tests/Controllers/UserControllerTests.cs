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

public class UserControllerTests
{
    private static readonly Guid AuthenticatedUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly Mock<IUserManager> _manager;
    private readonly UserController _controller;

    public UserControllerTests()
    {
        _manager = new Mock<IUserManager>();
        _controller = new UserController(_manager.Object);
        // Inject a ClaimsPrincipal so GetUserIdFromClaims() resolves.
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, AuthenticatedUserId.ToString())
                }, "TestScheme"))
            }
        };
    }

    [Fact]
    public async Task Register_WithValidModel_ReturnsOk()
    {
        var model = new UserRegisterModel { Username = "neo", Email = "neo@test.com", Password = "pass" };
        _manager.Setup(m => m.RegisterAsync(model, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IUser> { Result = new User { Username = "neo" } });

        var result = await _controller.Register(model, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Register_WithError_ReturnsBadRequest()
    {
        var model = new UserRegisterModel();
        _manager.Setup(m => m.RegisterAsync(model, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IUser> { IsError = true, Message = "Invalid" });

        var result = await _controller.Register(model, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsOk()
    {
        var model = new UserLoginModel { Email = "test@test.com", Password = "pass" };
        _manager.Setup(m => m.LoginAsync(model, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<string> { Result = "jwt_token" });

        var result = await _controller.Login(model, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ReturnsUnauthorized()
    {
        var model = new UserLoginModel();
        _manager.Setup(m => m.LoginAsync(model, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<string> { IsError = true });

        var result = await _controller.Login(model, null);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Get_Existing_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GetAsync(id, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IUser> { Result = new User() });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_NonExisting_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GetAsync(id, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IUser> { IsError = true, Result = null });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetAll_ReturnsOk()
    {
        _manager.Setup(m => m.GetAllAsync(It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = Array.Empty<IUser>() });

        var result = await _controller.GetAll(null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Update_WithError_ReturnsBadRequest()
    {
        var id = AuthenticatedUserId;
        _manager.Setup(m => m.UpdateAsync(id, It.IsAny<UserUpdateModel>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IUser> { IsError = true });

        var result = await _controller.Update(id, new UserUpdateModel(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_Success_ReturnsOk()
    {
        var id = AuthenticatedUserId;
        _manager.Setup(m => m.UpdateAsync(id, It.IsAny<UserUpdateModel>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IUser> { Result = new User() });

        var result = await _controller.Update(id, new UserUpdateModel(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Update_AnotherUsersId_IsNotFound_LikeDelete()
    {
        var result = await _controller.Update(Guid.NewGuid(), new UserUpdateModel { Title = "x" }, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
        _manager.Verify(m => m.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UserUpdateModel>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()), Times.Never);
    }

    [Fact]
    public async Task Delete_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeleteAsync(id, It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<bool> { Result = true });

        var result = await _controller.Delete(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Delete_Failure_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeleteAsync(id, It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<bool> { IsError = true, Result = false });

        var result = await _controller.Delete(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

}
