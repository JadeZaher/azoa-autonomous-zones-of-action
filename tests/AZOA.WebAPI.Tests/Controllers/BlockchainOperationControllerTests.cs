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

public class BlockchainOperationControllerTests
{
    private static readonly Guid AuthenticatedAvatarId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly Mock<IBlockchainOperationManager> _manager;
    private readonly BlockchainOperationController _controller;

    public BlockchainOperationControllerTests()
    {
        _manager = new Mock<IBlockchainOperationManager>();
        _controller = new BlockchainOperationController(_manager.Object);
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
        _manager.Setup(m => m.GetAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_NonExisting_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GetAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IBlockchainOperation> { IsError = true, Result = null });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetByAvatar_ReturnsOk()
    {
        var avatarId = AuthenticatedAvatarId;
        _manager.Setup(m => m.GetByAvatarAsync(avatarId, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IEnumerable<IBlockchainOperation>> { Result = Array.Empty<IBlockchainOperation>() });

        var result = await _controller.GetByAvatar(avatarId, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }
}
