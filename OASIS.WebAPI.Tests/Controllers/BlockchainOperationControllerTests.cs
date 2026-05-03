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

public class BlockchainOperationControllerTests
{
    private readonly Mock<IBlockchainOperationManager> _manager;
    private readonly BlockchainOperationController _controller;

    public BlockchainOperationControllerTests()
    {
        _manager = new Mock<IBlockchainOperationManager>();
        _controller = new BlockchainOperationController(_manager.Object);
    }

    [Fact]
    public async Task Get_Existing_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GetAsync(id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_NonExisting_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GetAsync(id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IBlockchainOperation> { IsError = true, Result = null });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetByAvatar_ReturnsOk()
    {
        var avatarId = Guid.NewGuid();
        _manager.Setup(m => m.GetByAvatarAsync(avatarId, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<IEnumerable<IBlockchainOperation>> { Result = Array.Empty<IBlockchainOperation>() });

        var result = await _controller.GetByAvatar(avatarId, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }
}
