// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class AllocationControllerTests
{
    [Fact]
    public async Task Allocate_NonApiKeyPrincipalWithApiKeyLookingClaims_IsForbidden()
    {
        var manager = new Mock<IAllocationManager>();
        var controller = CreateController(
            manager.Object,
            authMethod: null,
            new Claim("ApiKeyId", Guid.NewGuid().ToString()),
            new Claim("scope", AzoaScopes.NftMint));

        var result = await controller.Allocate(Guid.NewGuid(), new AllocationRequest());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        manager.Verify(candidate => candidate.AllocateAsync(
            It.IsAny<Guid>(),
            It.IsAny<AllocationRequest>(),
            It.IsAny<Guid>(),
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task Allocate_ApiKeyPrincipalUsesTheAuthenticatedAvatarClaim()
    {
        var manager = new Mock<IAllocationManager>();
        var apiKeyId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        Guid? capturedCaller = null;
        manager.Setup(candidate => candidate.AllocateAsync(
                It.IsAny<Guid>(),
                It.IsAny<AllocationRequest>(),
                It.IsAny<Guid>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<Guid?>()))
            .Callback<Guid, AllocationRequest, Guid, string?, string, Guid?>(
                (_, _, caller, _, _, _) => capturedCaller = caller)
            .ReturnsAsync(AZOAResult<AllocationResult>.Success(new AllocationResult()));
        var controller = CreateController(
            manager.Object,
            authMethod: "ApiKey",
            new Claim("ApiKeyId", apiKeyId.ToString()),
            new Claim("AvatarId", avatarId.ToString()),
            new Claim("scope", AzoaScopes.NftMint));

        var result = await controller.Allocate(Guid.NewGuid(), new AllocationRequest());

        result.Result.Should().BeOfType<OkObjectResult>();
        capturedCaller.Should().Be(avatarId);
    }

    private static AllocationController CreateController(
        IAllocationManager manager,
        string? authMethod,
        params Claim[] claims)
    {
        var allClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        if (authMethod is not null)
            allClaims.Add(new Claim("AuthMethod", authMethod));
        allClaims.AddRange(claims);

        return new AllocationController(manager)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(allClaims, "Test")),
                },
            },
        };
    }
}
