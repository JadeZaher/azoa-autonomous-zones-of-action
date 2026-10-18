using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
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
        // must carry a user-id claim or the controller fail-closes to Unauthorized.
        SetPrincipal(Guid.NewGuid());
    }

    /// <summary>Rebuilds the controller's authenticated principal for a given claim set.</summary>
    private void SetPrincipal(Guid userId, bool nodeOperatorSession = false, bool apiKey = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (nodeOperatorSession) claims.Add(new Claim(AzoaClaims.TokenUse, AzoaClaims.TokenUseNodeOperator));
        if (apiKey) claims.Add(new Claim("AuthMethod", "ApiKey"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"))
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
    public async Task Search_NodeAdminWithoutOperatorScope_BypassesDiscoverableGate()
    {
        // Owner correction 2026-10-18: "node admins will always be able to find users" —
        // deliberately NOT the "Operator" policy, so a node-operator session with no
        // "operator" scope and no acting-tenant claim still bypasses the Discoverable gate.
        SetPrincipal(Guid.NewGuid(), nodeOperatorSession: true);
        bool? observedIsNodeAdmin = null;
        _searchManager.Setup(m => m.SearchAsync(It.IsAny<SearchRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>(), It.IsAny<bool>()))
            .Callback<SearchRequest, Guid?, AZOARequest?, bool>((_, _, _, isNodeAdmin) => observedIsNodeAdmin = isNodeAdmin)
            .ReturnsAsync(new AZOAResult<SearchResult> { Result = new SearchResult() });

        await _controller.Search(new SearchRequest(), null);

        observedIsNodeAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task Search_NodeAdminViaApiKey_DoesNotBypassDiscoverableGate()
    {
        // Axis-1 rule shared with the "Operator" policy: an API key never carries node-admin
        // authority, even if its owner is a node admin.
        SetPrincipal(Guid.NewGuid(), nodeOperatorSession: true, apiKey: true);
        bool? observedIsNodeAdmin = null;
        _searchManager.Setup(m => m.SearchAsync(It.IsAny<SearchRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>(), It.IsAny<bool>()))
            .Callback<SearchRequest, Guid?, AZOARequest?, bool>((_, _, _, isNodeAdmin) => observedIsNodeAdmin = isNodeAdmin)
            .ReturnsAsync(new AZOAResult<SearchResult> { Result = new SearchResult() });

        await _controller.Search(new SearchRequest(), null);

        observedIsNodeAdmin.Should().BeFalse();
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
