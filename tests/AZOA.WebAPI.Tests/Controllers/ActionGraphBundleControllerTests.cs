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
using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Tests.Controllers;

public class ActionGraphBundleControllerTests
{
    private static readonly Guid AuthenticatedUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly Mock<IActionGraphBundleManager> _manager;
    private readonly ActionGraphBundleController _controller;

    public ActionGraphBundleControllerTests()
    {
        _manager = new Mock<IActionGraphBundleManager>();
        _controller = new ActionGraphBundleController(_manager.Object);
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
    public async Task Get_Existing_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GetAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = new ActionGraphBundle() });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_NonExisting_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GetAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IActionGraphBundle> { IsError = true, Result = null });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetAll_ReturnsOk()
    {
        _manager.Setup(m => m.GetAllAsync(It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IEnumerable<IActionGraphBundle>> { Result = Array.Empty<IActionGraphBundle>() });

        var result = await _controller.GetAll(null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CreateOrUpdate_Success_ReturnsOk()
    {
        var model = new ActionGraphBundleCreateModel { Name = "Test" };
        _manager.Setup(m => m.CreateOrUpdateAsync(model, AuthenticatedUserId, null, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = new ActionGraphBundle() });

        var result = await _controller.CreateOrUpdate(model, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CreateOrUpdate_Error_ReturnsBadRequest()
    {
        var model = new ActionGraphBundleCreateModel();
        _manager.Setup(m => m.CreateOrUpdateAsync(model, AuthenticatedUserId, null, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IActionGraphBundle> { IsError = true, Message = "boom" });

        var result = await _controller.CreateOrUpdate(model, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateOrUpdate_NoAuthClaim_ReturnsUnauthorized()
    {
        // Wipe the principal -- simulate a request where no user claim is present.
        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _controller.CreateOrUpdate(new ActionGraphBundleCreateModel(), null);

        result.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Delete_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeleteAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<bool> { Result = true });

        var result = await _controller.Delete(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Delete_Failure_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeleteAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<bool> { IsError = true, Result = false });

        var result = await _controller.Delete(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Generate_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GenerateAsync(id, It.IsAny<ActionGraphBundleDappGenerationRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
                .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = new ActionGraphBundle() });

        var result = await _controller.Generate(id, new ActionGraphBundleDappGenerationRequest(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Generate_Error_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GenerateAsync(id, It.IsAny<ActionGraphBundleDappGenerationRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
                .ReturnsAsync(new AZOAResult<IActionGraphBundle> { IsError = true });

        var result = await _controller.Generate(id, new ActionGraphBundleDappGenerationRequest(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Deploy_Pending_ReturnsAccepted()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeployAsync(id, It.IsAny<DeploymentStartRequest>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<DeploymentReceipt>.Success(new("operation", "artifact", "hash", new("NativeContract", "Solana", "Devnet"), "Prepared", "CreateBuffer", null, null, [], null, 1)));

        var result = await _controller.Deploy(id, new("artifact", "hash", new("NativeContract", "Solana", "Devnet")), default);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(202);
    }

    [Fact]
    public async Task Deploy_Error_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeployAsync(id, It.IsAny<DeploymentStartRequest>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<DeploymentReceipt>.Failure("Invalid artifact"));

        var result = await _controller.Deploy(id, new("artifact", "hash", new("NativeContract", "Solana", "Devnet")), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        var model = new ActionGraphBundleCreateModel { Name = "Updated" };
        _manager.Setup(m => m.CreateOrUpdateAsync(model, AuthenticatedUserId, id, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = new ActionGraphBundle() });

        var result = await _controller.Update(id, model, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Update_Error_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        var model = new ActionGraphBundleCreateModel();
        _manager.Setup(m => m.CreateOrUpdateAsync(model, AuthenticatedUserId, id, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IActionGraphBundle> { IsError = true, Message = "boom" });

        var result = await _controller.Update(id, model, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_ForbiddenMarker_Returns403()
    {
        // Controller translates the ActionGraphBundle_FORBIDDEN: prefix to 403 Forbidden.
        var id = Guid.NewGuid();
        var model = new ActionGraphBundleCreateModel { Name = "X" };
        _manager.Setup(m => m.CreateOrUpdateAsync(model, AuthenticatedUserId, id, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IActionGraphBundle>
                {
                    IsError = true,
                    Message = ActionGraphBundleAuthorizationError.Forbidden + "owned by another user"
                });

        var result = await _controller.Update(id, model, null);

        var status = result.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Update_NotFoundMarker_Returns404()
    {
        var id = Guid.NewGuid();
        var model = new ActionGraphBundleCreateModel { Name = "X" };
        _manager.Setup(m => m.CreateOrUpdateAsync(model, AuthenticatedUserId, id, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IActionGraphBundle>
                {
                    IsError = true,
                    Message = ActionGraphBundleAuthorizationError.NotFound + "no such record"
                });

        var result = await _controller.Update(id, model, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Update_NoAuthClaim_ReturnsUnauthorized()
    {
        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _controller.Update(Guid.NewGuid(), new ActionGraphBundleCreateModel(), null);

        result.Result.Should().BeOfType<UnauthorizedResult>();
    }
}
