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

using AZOA.WebAPI.Services.Reserve;

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
            It.IsAny<Guid?>(),
            It.IsAny<ReserveDrawAuthority?>()), Times.Never);
    }

    [Fact]
    public async Task Allocate_ApiKeyPrincipalUsesTheAuthenticatedUserClaim()
    {
        var manager = new Mock<IAllocationManager>();
        var apiKeyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        Guid? capturedCaller = null;
        manager.Setup(candidate => candidate.AllocateAsync(
                It.IsAny<Guid>(),
                It.IsAny<AllocationRequest>(),
                It.IsAny<Guid>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<Guid?>(),
                It.IsAny<ReserveDrawAuthority?>()))
            .Callback<Guid, AllocationRequest, Guid, string?, string, Guid?, ReserveDrawAuthority?>(
                (_, _, caller, _, _, _, _) => capturedCaller = caller)
            .ReturnsAsync(AZOAResult<AllocationResult>.Success(new AllocationResult()));
        var controller = CreateController(
            manager.Object,
            authMethod: "ApiKey",
            new Claim("ApiKeyId", apiKeyId.ToString()),
            new Claim("UserId", userId.ToString()),
            new Claim("scope", AzoaScopes.NftMint));

        var result = await controller.Allocate(Guid.NewGuid(), new AllocationRequest());

        result.Result.Should().BeOfType<OkObjectResult>();
        capturedCaller.Should().Be(userId);
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

        // W3.6 added an edge consent check: with no gate the controller REFUSES (503)
        // rather than waving the allocation through, so these principal-shape tests have
        // to supply a gate that says yes. What the gate itself does under a real grant is
        // covered by AllocationConsentGateTests, not here.
        return new AllocationController(manager, PermissiveConsentGate())
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

    private static ITenantConsentGate PermissiveConsentGate()
    {
        var gate = new Mock<ITenantConsentGate>();
        gate.Setup(candidate => candidate.EnsureAllowedAsync(
                It.IsAny<SigningContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        return gate.Object;
    }
}
