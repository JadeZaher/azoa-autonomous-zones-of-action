using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class NodeOperatorRecoveryTests
{
    [Fact]
    public async Task Login_DependencyFailure_IsServiceUnavailableNotCredentialFailure()
    {
        var manager = new Mock<INodeOperatorManager>();
        manager.Setup(item => item.LoginAsync(
                It.IsAny<NodeOperatorLoginRequest>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<NodeOperatorSessionResponse>.FailureWithCode(
                "Node operator sign-in is temporarily unavailable.",
                NodeOperatorErrorCodes.ServiceUnavailable));
        var controller = new NodeOperatorSessionController(manager.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var response = await controller.Login(new NodeOperatorLoginRequest(), CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task OperatorControl_DependencyFailure_IsServiceUnavailable()
    {
        var manager = new Mock<IKycControlPlaneManager>();
        manager.Setup(item => item.ListProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<KycProviderProfileResponse>>.FailureWithCode(
                "KYC control plane is temporarily unavailable.",
                AzoaErrorCodes.DependencyUnavailable));
        var controller = new NodeOperatorController(manager.Object);

        var response = await controller.ListProviders(CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task TenantControl_DependencyFailure_IsServiceUnavailable()
    {
        var tenantId = Guid.NewGuid();
        var manager = new Mock<IKycControlPlaneManager>();
        manager.Setup(item => item.ListTenantChoicesAsync(
                tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<TenantKycProviderChoiceResponse>>.FailureWithCode(
                "KYC control plane is temporarily unavailable.",
                AzoaErrorCodes.DependencyUnavailable));
        var controller = new TenantKycConfigurationController(manager.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("sub", tenantId.ToString("D"))],
                        "test")),
                },
            },
        };

        var response = await controller.ListProviders(CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }
}
