using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Kyb;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AZOA.WebAPI.Tests.Kyb;

public sealed class TenantBusinessVerificationControllerTests
{
    [Fact]
    public async Task TenantComesOnlyFromAuthenticatedSubject()
    {
        var tenantId = Guid.NewGuid();
        var service = new Mock<IKybService>();
        service.Setup(value => value.GetStatusAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KybStatusResponse?>.Success(null));
        var controller = NewController(service.Object, new Claim(ClaimTypes.NameIdentifier, tenantId.ToString()),
            new Claim("tenant_id", Guid.NewGuid().ToString()));
        (await controller.Status(default)).Should().BeOfType<OkObjectResult>();
        service.Verify(value => value.GetStatusAsync(tenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiKeyWithoutSubmitScopeCannotBegin()
    {
        var service = new Mock<IKybService>(MockBehavior.Strict);
        var controller = NewController(service.Object, new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("AuthMethod", "ApiKey"), new Claim("scope", "tenant:provision"));
        (await controller.Begin("key", default)).Should().BeOfType<ForbidResult>();
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingSubjectCannotReadStatus()
    {
        var service = new Mock<IKybService>(MockBehavior.Strict);
        var controller = NewController(service.Object);
        (await controller.Status(default)).Should().BeOfType<UnauthorizedResult>();
    }

    private static TenantBusinessVerificationController NewController(IKybService service, params Claim[] claims)
        => new(service) { ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        } };
}
