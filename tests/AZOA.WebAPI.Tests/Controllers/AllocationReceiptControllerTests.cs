// SPDX-License-Identifier: UNLICENSED

using System.Reflection;
using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Moq;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class AllocationReceiptControllerTests
{
    [Fact]
    public async Task Get_ScopedApiKey_UsesClaimsAndRequiredHeader()
    {
        var manager = new Mock<IAllocationReceiptManager>();
        var apiKeyId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        AllocationReceiptRequest? captured = null;
        manager.Setup(candidate => candidate.GetAsync(
                It.IsAny<AllocationReceiptRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<AllocationReceiptRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(AZOAResult<AllocationReceiptResponse>.Success(new AllocationReceiptResponse()));
        var controller = CreateController(manager.Object, apiKeyId, avatarId, AzoaScopes.NftMint);

        var result = await controller.Get("  payment-intent-42  ", CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        captured.Should().NotBeNull();
        captured!.ApiKeyId.Should().Be(apiKeyId);
        captured.CallerAvatarId.Should().Be(avatarId);
        captured.ClientIdempotencyKey.Should().Be("payment-intent-42");
    }

    [Fact]
    public async Task Get_MissingHeader_ReturnsBadRequestWithoutManagerCall()
    {
        var manager = new Mock<IAllocationReceiptManager>();
        var controller = CreateController(manager.Object, Guid.NewGuid(), Guid.NewGuid(), AzoaScopes.NftMint);

        var result = await controller.Get(null, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        manager.Verify(candidate => candidate.GetAsync(
            It.IsAny<AllocationReceiptRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Get_ReceiptNotFound_ReturnsNotFoundWithoutDistinguishingReason()
    {
        var manager = new Mock<IAllocationReceiptManager>();
        manager.Setup(candidate => candidate.GetAsync(
                It.IsAny<AllocationReceiptRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<AllocationReceiptResponse>.FailureWithCode(
                "Allocation receipt not found.", AzoaErrorCodes.NotFound));
        var controller = CreateController(manager.Object, Guid.NewGuid(), Guid.NewGuid(), AzoaScopes.NftMint);

        var result = await controller.Get("payment-intent-42", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Reconcile_DependencyUnavailable_ReturnsServiceUnavailable()
    {
        var manager = new Mock<IAllocationReceiptManager>();
        manager.Setup(candidate => candidate.ReconcileAsync(
                It.IsAny<AllocationReceiptRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<AllocationReceiptResponse>.FailureWithCode(
                "Allocation receipt service is temporarily unavailable. Try again later.",
                AzoaErrorCodes.DependencyUnavailable));
        var controller = CreateController(manager.Object, Guid.NewGuid(), Guid.NewGuid(), AzoaScopes.NftMint);

        var result = await controller.Reconcile("payment-intent-42", CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode
            .Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Reconcile_MissingMintScope_ReturnsForbiddenWithoutManagerCall()
    {
        var manager = new Mock<IAllocationReceiptManager>();
        var controller = CreateController(manager.Object, Guid.NewGuid(), Guid.NewGuid());

        var result = await controller.Reconcile("payment-intent-42", CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        manager.Verify(candidate => candidate.ReconcileAsync(
            It.IsAny<AllocationReceiptRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ReceiptEndpoints_UseTheFinancialRateLimit()
    {
        GetRateLimitPolicy(nameof(AllocationReceiptController.Get)).Should().Be("financial");
        GetRateLimitPolicy(nameof(AllocationReceiptController.Reconcile)).Should().Be("financial");
    }

    private static AllocationReceiptController CreateController(
        IAllocationReceiptManager manager,
        Guid apiKeyId,
        Guid avatarId,
        params string[] scopes)
    {
        var claims = new List<Claim>
        {
            new("AuthMethod", "ApiKey"),
            new("ApiKeyId", apiKeyId.ToString()),
            new("AvatarId", avatarId.ToString()),
        };
        claims.AddRange(scopes.Select(scope => new Claim("scope", scope)));
        return new AllocationReceiptController(manager)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestApiKey")),
                },
            },
        };
    }

    private static string? GetRateLimitPolicy(string actionName)
        => typeof(AllocationReceiptController)
            .GetMethod(actionName, BindingFlags.Instance | BindingFlags.Public)
            ?.GetCustomAttribute<EnableRateLimitingAttribute>()
            ?.PolicyName;
}
