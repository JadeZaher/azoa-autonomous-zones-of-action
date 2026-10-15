using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class ApiKeyControllerTenantIssuanceTests
{
    [Fact]
    public void CredentialManagement_RequiresFirstPartyLogin()
    {
        typeof(ApiKeyController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Should().ContainSingle(attribute => attribute.Policy == "FirstPartyLogin");
    }

    [Fact]
    public async Task Create_RejectsLegacyUnscopedKey()
    {
        var keys = new Mock<IApiKeyStore>();
        var controller = new ApiKeyController(keys.Object, Mock.Of<IUserStore>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(
                        new System.Security.Claims.ClaimsIdentity(new[]
                        {
                            new System.Security.Claims.Claim(
                                System.Security.Claims.ClaimTypes.NameIdentifier,
                                Guid.NewGuid().ToString())
                        }, "test"))
                }
            }
        };

        var result = await controller.Create(new CreateApiKeyRequest
        {
            Name = "unsafe legacy key",
            Scopes = null
        });

        result.Should().BeOfType<BadRequestObjectResult>();
        keys.Verify(store => store.CreateAsync(
            It.IsAny<ApiKey>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTenantKey_BindsExistingUserToFixedNonValueScopes()
    {
        var tenantId = Guid.NewGuid();
        ApiKey? persisted = null;
        var keys = new Mock<IApiKeyStore>();
        keys.Setup(store => store.CreateAsync(It.IsAny<ApiKey>(), It.IsAny<CancellationToken>()))
            .Callback((ApiKey key, CancellationToken _) => persisted = key)
            .Returns(Task.CompletedTask);
        var users = new Mock<IUserStore>();
        users.Setup(store => store.GetByIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IUser>.Success(new User { Id = tenantId }));
        var controller = new ApiKeyController(keys.Object, users.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var action = await controller.CreateTenantKey(new CreateTenantApiKeyRequest
        {
            TenantUserId = tenantId,
            Name = "Arda integration",
            ExpiresInDays = 30
        });

        var ok = action.Should().BeOfType<OkObjectResult>().Subject;
        var envelope = ok.Value.Should()
            .BeOfType<AZOAResult<CreateApiKeyResponse>>().Subject;
        persisted.Should().NotBeNull();
        persisted!.UserId.Should().Be(tenantId);
        persisted.Scopes.Should().Be(
            "tenant:provision,wallet:manage,kyc:read,kyc:submit");
        persisted.Scopes.Should().NotContain(AzoaScopes.NftMint);
        persisted.Scopes.Should().NotContain(AzoaScopes.TransferSign);
        envelope.Result!.Key.Should().StartWith("azoa_");
        envelope.Result.Key.Should().NotBe(persisted.KeyHash);
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Fact]
    public void CreateTenantKey_IsOperatorPolicyOnly()
    {
        var method = typeof(ApiKeyController).GetMethod(nameof(ApiKeyController.CreateTenantKey));

        method.Should().NotBeNull();
        method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Should().ContainSingle(attribute => attribute.Policy == "Operator");
    }

    [Fact]
    public async Task CreateTenantKey_IdentityStoreFailureReturnsServiceUnavailable()
    {
        var tenantId = Guid.NewGuid();
        var keys = new Mock<IApiKeyStore>();
        var users = new Mock<IUserStore>();
        users.Setup(store => store.GetByIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IUser>.Failure(
                "USER_STORE_UNAVAILABLE: Identity persistence is temporarily unavailable."));
        var controller = new ApiKeyController(keys.Object, users.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var action = await controller.CreateTenantKey(new CreateTenantApiKeyRequest
        {
            TenantUserId = tenantId,
            Name = "Arda integration"
        });

        var unavailable = action.Should().BeOfType<ObjectResult>().Subject;
        unavailable.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        keys.Verify(store => store.CreateAsync(
            It.IsAny<ApiKey>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
