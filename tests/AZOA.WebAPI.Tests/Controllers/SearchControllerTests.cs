using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Controllers;

public class SearchControllerTests
{
    private readonly Mock<ISearchManager> _searchManager;
    private readonly SearchController _controller;

    public SearchControllerTests()
    {
        _searchManager = new Mock<ISearchManager>();
        _controller = new SearchController(_searchManager.Object);
        // H-2: Search now binds scope to the authenticated caller, so the principal
        // must carry an avatar-id claim or the controller fail-closes to Unauthorized.
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
    public async Task Search_ReturnsOk()
    {
        _searchManager.Setup(m => m.SearchAsync(It.IsAny<SearchRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync(new AZOAResult<SearchResult> { Result = new SearchResult() });

        var result = await _controller.Search(new SearchRequest(), null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Search_Error_ReturnsBadRequest()
    {
        _searchManager.Setup(m => m.SearchAsync(It.IsAny<SearchRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync(new AZOAResult<SearchResult> { IsError = true, Message = "Error" });

        var result = await _controller.Search(new SearchRequest(), null);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetFacets_ReturnsOk()
    {
        _searchManager.Setup(m => m.GetFacetsAsync(It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync(new AZOAResult<List<SearchFacet>> { Result = new List<SearchFacet>() });

        var result = await _controller.GetFacets(null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }
}
