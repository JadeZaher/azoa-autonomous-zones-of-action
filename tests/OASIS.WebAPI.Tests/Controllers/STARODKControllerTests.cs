using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using OASIS.WebAPI.Controllers;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests.Controllers;

public class STARODKControllerTests
{
    private static readonly Guid AuthenticatedAvatarId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly Mock<ISTARManager> _manager;
    private readonly STARODKController _controller;

    public STARODKControllerTests()
    {
        _manager = new Mock<ISTARManager>();
        _controller = new STARODKController(_manager.Object);
        // Inject a ClaimsPrincipal so GetAvatarIdFromClaims() resolves.
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, AuthenticatedAvatarId.ToString())
                }, "TestScheme"))
            }
        };
    }

    [Fact]
    public async Task Get_Existing_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GetAsync(id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { Result = new STARODK() });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_NonExisting_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GetAsync(id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { IsError = true, Result = null });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetAll_ReturnsOk()
    {
        _manager.Setup(m => m.GetAllAsync(It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IEnumerable<ISTARODK>> { Result = Array.Empty<ISTARODK>() });

        var result = await _controller.GetAll(null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CreateOrUpdate_Success_ReturnsOk()
    {
        var model = new STARODKCreateModel { Name = "Test" };
        _manager.Setup(m => m.CreateOrUpdateAsync(model, AuthenticatedAvatarId, null, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { Result = new STARODK() });

        var result = await _controller.CreateOrUpdate(model, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CreateOrUpdate_Error_ReturnsBadRequest()
    {
        var model = new STARODKCreateModel();
        _manager.Setup(m => m.CreateOrUpdateAsync(model, AuthenticatedAvatarId, null, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { IsError = true, Message = "boom" });

        var result = await _controller.CreateOrUpdate(model, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateOrUpdate_NoAuthClaim_ReturnsUnauthorized()
    {
        // Wipe the principal -- simulate a request where no avatar claim is present.
        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _controller.CreateOrUpdate(new STARODKCreateModel(), null);

        result.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Delete_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeleteAsync(id, It.IsAny<Guid?>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<bool> { Result = true });

        var result = await _controller.Delete(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Delete_Failure_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeleteAsync(id, It.IsAny<Guid?>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<bool> { IsError = true, Result = false });

        var result = await _controller.Delete(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Generate_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GenerateAsync(id, It.IsAny<STARDappGenerationRequest>(), It.IsAny<Guid?>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { Result = new STARODK() });

        var result = await _controller.Generate(id, new STARDappGenerationRequest(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Generate_Error_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GenerateAsync(id, It.IsAny<STARDappGenerationRequest>(), It.IsAny<Guid?>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { IsError = true });

        var result = await _controller.Generate(id, new STARDappGenerationRequest(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Deploy_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeployAsync(id, It.IsAny<Guid?>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { Result = new STARODK() });

        var result = await _controller.Deploy(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Deploy_Error_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeployAsync(id, It.IsAny<Guid?>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { IsError = true });

        var result = await _controller.Deploy(id, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        var model = new STARODKCreateModel { Name = "Updated" };
        _manager.Setup(m => m.CreateOrUpdateAsync(model, AuthenticatedAvatarId, id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { Result = new STARODK() });

        var result = await _controller.Update(id, model, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Update_Error_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        var model = new STARODKCreateModel();
        _manager.Setup(m => m.CreateOrUpdateAsync(model, AuthenticatedAvatarId, id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { IsError = true, Message = "boom" });

        var result = await _controller.Update(id, model, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_ForbiddenMarker_Returns403()
    {
        // Controller translates the STARODK_FORBIDDEN: prefix to 403 Forbidden.
        var id = Guid.NewGuid();
        var model = new STARODKCreateModel { Name = "X" };
        _manager.Setup(m => m.CreateOrUpdateAsync(model, AuthenticatedAvatarId, id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK>
                {
                    IsError = true,
                    Message = STARODKAuthorizationError.Forbidden + "owned by another avatar"
                });

        var result = await _controller.Update(id, model, null);

        var status = result.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Update_NotFoundMarker_Returns404()
    {
        var id = Guid.NewGuid();
        var model = new STARODKCreateModel { Name = "X" };
        _manager.Setup(m => m.CreateOrUpdateAsync(model, AuthenticatedAvatarId, id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK>
                {
                    IsError = true,
                    Message = STARODKAuthorizationError.NotFound + "no such record"
                });

        var result = await _controller.Update(id, model, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Update_NoAuthClaim_ReturnsUnauthorized()
    {
        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _controller.Update(Guid.NewGuid(), new STARODKCreateModel(), null);

        result.Result.Should().BeOfType<UnauthorizedResult>();
    }
}
