// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Consent;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Fiat;

/// <summary>Tenant-driven add-funds is consent-probed with the issuance scope it funds; see Controllers/AGENTS.md §fiat-checkout-consent.</summary>
public sealed class FiatCheckoutConsentScopeTests
{
    private readonly Mock<IConsentGrantStore> _grants = new();
    private readonly Mock<IConsentAuditStore> _audit = new();
    private readonly Mock<IValueRailProvider> _railProvider = new();
    private readonly Mock<IFiatOnRampRail> _rail;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _payerId = Guid.NewGuid();

    public FiatCheckoutConsentScopeTests()
    {
        _rail = _railProvider.As<IFiatOnRampRail>();
        _audit.Setup(a => a.AppendAsync(It.IsAny<ConsentAuditEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        _grants.Setup(g => g.FindCoveringGrantAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ConsentGrant> { Result = null });
        _rail.Setup(r => r.BeginCheckoutAsync(It.IsAny<FiatCheckoutRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FiatCheckoutRequest request, CancellationToken _) => AZOAResult<FiatCheckoutIntent>.Success(
                new FiatCheckoutIntent { IntentId = Guid.NewGuid(), AdmissionKey = request.AdmissionKey }));
    }

    [Theory]
    [InlineData(AzoaScopes.UnitsIssue, true)]
    [InlineData(AzoaScopes.TransferSign, false)]
    [InlineData(AzoaScopes.NftMint, false)]
    public async Task Tenant_checkout_is_admitted_only_by_the_issuance_scope(string grantedScope, bool admitted)
    {
        GivenParticipationGrant(grantedScope);

        var response = await Controller().Start(_payerId, TopUp());

        if (admitted)
        {
            var started = response.Result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<AZOAResult<FiatCheckoutIntent>>().Subject;
            started.Result!.AdmissionKey.Should().Be($"{_payerId:N}:{_tenantId:N}:top-up-25");
            _rail.Verify(r => r.BeginCheckoutAsync(
                It.Is<FiatCheckoutRequest>(request => request.SubjectUserId == _payerId && request.TenantId == _tenantId),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            response.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            _rail.Verify(r => r.BeginCheckoutAsync(It.IsAny<FiatCheckoutRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    private void GivenParticipationGrant(string scope)
        => _grants.Setup(g => g.FindCoveringGrantAsync(
                _payerId, _tenantId, scope, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ConsentGrant>
            {
                Result = new ConsentGrant
                {
                    GrantorUserId = _payerId,
                    TenantId = _tenantId,
                    Origin = GrantOrigin.Participation,
                    Scopes = new List<string> { scope },
                },
            });

    private static FiatCheckoutStartRequest TopUp() => new()
    {
        RequestKey = "top-up-25",
        AmountMinorUnits = 2500,
        SuccessUrl = "http://localhost:3000/wallet",
        CancelUrl = "http://localhost:3000/wallet",
    };

    private FiatCheckoutController Controller()
    {
        var gate = new TenantConsentGate(_grants.Object, _audit.Object,
            new AZOA.WebAPI.Tests.TestSupport.ApprovedKybGate(), NullLogger<TenantConsentGate>.Instance);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, _tenantId.ToString()),
            new("UserId", _tenantId.ToString()),
            new("AuthMethod", "ApiKey"),
            new("ApiKeyId", Guid.NewGuid().ToString()),
            new("scope", AzoaScopes.TenantProvision),
        };
        IValueRailProvider? provider = _railProvider.Object;
        var factory = new Mock<IValueRailProviderFactory>();
        factory.Setup(f => f.TryGetRailProvider(FiatRailIds.UsdRail, It.IsAny<RailEnvironment>(), out provider)).Returns(true);
        return new FiatCheckoutController(factory.Object, new FiatOptions(), gate)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }
}
