using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class KycControllerAuthorizationTests
{
    [Theory]
    [InlineData(nameof(KycController.Submit))]
    [InlineData(nameof(KycController.GetStatus))]
    [InlineData(nameof(KycController.GetById))]
    [InlineData(nameof(KycController.GetDocuments))]
    public void DetailedKycRoutes_RequireFirstPartyLogin(string action)
        => PoliciesOn(typeof(KycController).GetMethod(action)!)
            .Should().Contain("FirstPartyLogin");

    [Fact]
    public async Task Summary_ApiKeyWithoutKycRead_IsForbiddenBeforeManagerCall()
    {
        var manager = new Mock<IKycManager>();
        var controller = BuildController(manager.Object, ApiKeyPrincipal(AzoaScopes.WalletManage));

        var response = await controller.GetStatusSummary(CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        manager.Verify(
            service => service.GetStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Summary_ApiKeyWithKycRead_ReturnsOnlyMinimalProjection()
    {
        var avatarId = Guid.NewGuid();
        var manager = new Mock<IKycManager>();
        manager.Setup(service => service.GetStatusAsync(avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmissionModel>.Success(new KycSubmissionModel
            {
                AvatarId = avatarId,
                ProviderKey = "provider-internal",
                ProviderSessionId = "provider-session-secret-reference",
                ReviewerId = Guid.NewGuid().ToString(),
                Status = KycStatus.APPROVED,
                SubmittedAt = DateTime.UtcNow,
            }));
        var controller = BuildController(manager.Object, ApiKeyPrincipal(AzoaScopes.KycRead, avatarId));

        var response = await controller.GetStatusSummary(CancellationToken.None);

        var envelope = response.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<AZOAResult<KycStatusSummaryResponse>>()
            .Subject;
        envelope.Result.Should().NotBeNull();
        envelope.Result!.IsVerified.Should().BeTrue();
    }

    private static KycController BuildController(IKycManager manager, ClaimsPrincipal principal)
        => new(manager)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };

    private static ClaimsPrincipal ApiKeyPrincipal(string scope, Guid? avatarId = null)
        => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, (avatarId ?? Guid.NewGuid()).ToString()),
            new Claim("AuthMethod", "ApiKey"),
            new Claim("scope", scope),
        ], "test"));

    private static IEnumerable<string?> PoliciesOn(System.Reflection.MemberInfo member)
        => member.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy);
}
