using System.Reflection;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core.Diagnostics;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class NodeTransparencyControllerTests
{
    [Fact]
    public async Task GetCurrent_ManagerError_ReturnsGenericNoStore503()
    {
        var manager = new Mock<INodeTransparencyManager>();
        manager.Setup(value => value.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeTransparencySnapshotResponse>
            {
                IsError = true,
                Message = "private database endpoint failed",
            });
        var controller = new NodeTransparencyController(manager.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        var response = await controller.GetCurrent(CancellationToken.None);

        var objectResult = response.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var body = objectResult.Value.Should()
            .BeOfType<AZOAResult<NodeTransparencySnapshotResponse>>()
            .Subject;
        body.Message.Should().Be(NodeTransparencyMessages.Unavailable);
        body.Message.Should().NotContain("database");
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Fact]
    public void Controller_AlwaysSuppressesDebugExceptionDetails()
    {
        typeof(NodeTransparencyController)
            .GetCustomAttribute<SuppressDebugExceptionDetailsAttribute>()
            .Should().NotBeNull();
    }

    [Fact]
    public async Task GetAuditHistoryCheckpoint_ManagerError_ReturnsGenericNoStore503()
    {
        var manager = new Mock<INodeTransparencyManager>();
        manager.Setup(value => value.GetAuditHistoryCheckpointAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeTransparencyHistoryDocument>
            {
                IsError = true,
                Message = "protected checkpoint storage failed",
            });
        var controller = new NodeTransparencyController(manager.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var response = await controller.GetAuditHistoryCheckpoint(CancellationToken.None);

        var objectResult = response.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var body = objectResult.Value.Should()
            .BeOfType<AZOAResult<NodeTransparencyHistoryDocument>>()
            .Subject;
        body.Message.Should().Be(NodeTransparencyMessages.Unavailable);
        body.Message.Should().NotContain("storage");
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }
}
