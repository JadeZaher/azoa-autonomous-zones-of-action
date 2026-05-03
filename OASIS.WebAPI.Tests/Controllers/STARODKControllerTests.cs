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

public class STARODKControllerTests
{
    private readonly Mock<ISTARManager> _manager;
    private readonly STARODKController _controller;

    public STARODKControllerTests()
    {
        _manager = new Mock<ISTARManager>();
        _controller = new STARODKController(_manager.Object);
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
        _manager.Setup(m => m.CreateOrUpdateAsync(model, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { Result = new STARODK() });

        var result = await _controller.CreateOrUpdate(model, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CreateOrUpdate_Error_ReturnsBadRequest()
    {
        var model = new STARODKCreateModel();
        _manager.Setup(m => m.CreateOrUpdateAsync(model, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { IsError = true });

        var result = await _controller.CreateOrUpdate(model, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
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
    public async Task Generate_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GenerateAsync(id, It.IsAny<STARDappGenerationRequest>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { Result = new STARODK() });

        var result = await _controller.Generate(id, new STARDappGenerationRequest(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Generate_Error_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GenerateAsync(id, It.IsAny<STARDappGenerationRequest>(), It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { IsError = true });

        var result = await _controller.Generate(id, new STARDappGenerationRequest(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Deploy_Success_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeployAsync(id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { Result = new STARODK() });

        var result = await _controller.Deploy(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Deploy_Error_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.DeployAsync(id, It.IsAny<OASISRequest?>()))
                .ReturnsAsync(new OASISResult<ISTARODK> { IsError = true });

        var result = await _controller.Deploy(id, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }
}
