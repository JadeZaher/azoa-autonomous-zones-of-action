using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class TenantControllerRetryableResultTests
{
    [Fact]
    public async Task EnsureCustodialAccount_InProgress_ReturnsConflict()
    {
        var tenantId = Guid.NewGuid();
        var custodial = new Mock<ITenantCustodialAccountManager>();
        var failure = AZOAResult<TenantCustodialAccountStatusResponse>.Failure(
            TenantCustodialOperationError.CustodyInProgress + "Still running.");
        custodial.Setup(manager => manager.EnsureAsync(
                tenantId,
                "user-42",
                "stable-key",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(failure);
        var controller = BuildController(
            tenantId,
            custodial.Object,
            AzoaScopes.WalletManage,
            AzoaScopes.KycRead);

        var response = await controller.EnsureCustodialAccount("user-42", "stable-key");

        var conflict = response.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        conflict.Value.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task BeginCustodialKyc_InProgress_ReturnsConflict()
    {
        var tenantId = Guid.NewGuid();
        var custodial = new Mock<ITenantCustodialAccountManager>();
        var failure = AZOAResult<TenantKycSessionResponse>.Failure(
            TenantCustodialOperationError.KycSessionInProgress + "Still running.");
        custodial.Setup(manager => manager.BeginKycAsync(
                tenantId,
                "user-42",
                "stable-key",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(failure);
        var controller = BuildController(
            tenantId,
            custodial.Object,
            AzoaScopes.KycSubmit);

        var response = await controller.BeginCustodialKyc("user-42", "stable-key");

        var conflict = response.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        conflict.Value.Should().BeSameAs(failure);
    }

    private static TenantController BuildController(
        Guid tenantId,
        ITenantCustodialAccountManager custodial,
        params string[] scopes)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, tenantId.ToString())
        };
        claims.AddRange(scopes.Select(scope => new Claim("scope", scope)));

        return new TenantController(Mock.Of<ITenantManager>(), custodial)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
    }
}
