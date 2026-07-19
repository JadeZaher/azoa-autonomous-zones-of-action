using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class BridgeControllerIdentityTests
{
    [Fact]
    public async Task GetHistory_NonGuidSubject_IsUnauthorizedWithoutServiceCall()
    {
        var service = new Mock<ICrossChainBridgeService>();
        var controller = CreateController(service, new Claim(ClaimTypes.NameIdentifier, "not-a-guid"));

        var result = await controller.GetHistory(CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        service.Verify(candidate => candidate.GetBridgeHistoryAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetHistory_MalformedAvatarClaim_DoesNotFallBackToSubject()
    {
        var service = new Mock<ICrossChainBridgeService>();
        var controller = CreateController(
            service,
            new Claim("avatarId", "malformed"),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));

        var result = await controller.GetHistory(CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        service.Verify(candidate => candidate.GetBridgeHistoryAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetHistory_ValidGuidSubject_UsesExactAuthenticatedIdentity()
    {
        var avatarId = Guid.NewGuid();
        var service = new Mock<ICrossChainBridgeService>();
        service.Setup(candidate => candidate.GetBridgeHistoryAsync(
                avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<BridgeTransactionResult>>.Success(
                Array.Empty<BridgeTransactionResult>()));
        var controller = CreateController(
            service,
            new Claim(ClaimTypes.NameIdentifier, avatarId.ToString()));

        var result = await controller.GetHistory(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        service.Verify(candidate => candidate.GetBridgeHistoryAsync(
            avatarId, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static BridgeController CreateController(
        Mock<ICrossChainBridgeService> service,
        params Claim[] claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return new BridgeController(service.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }
}
