using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Consent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AZOA.WebAPI.Tests.Consent;

public sealed class TenantProvisionedGrantTests
{
    [Fact]
    public void Provisioning_scope_ceiling_allows_inbound_credit_without_outbound_signing()
    {
        TenantProvisionedGrant.CarriesNoValueSigningScope.Should().BeTrue();
        TenantProvisionedGrant.ImpliedScopes.Should().Contain(AzoaScopes.NftMint);
        TenantProvisionedGrant.ImpliedScopes.Should().NotIntersectWith(AzoaScopes.ValueSigningScopes);
        AzoaScopes.ParticipationInboundCreditFloor.Should().NotIntersectWith(AzoaScopes.ValueSigningScopes);
    }

    [Fact]
    public void Participation_reference_is_stable_and_tenant_scoped()
    {
        var tenant = Guid.NewGuid();
        var original = TenantProvisionedGrant.ParticipationRefFor(tenant, "user/42");
        TenantProvisionedGrant.ParticipationRefFor(tenant, "user/42").Should().Be(original);
        TenantProvisionedGrant.ParticipationRefFor(Guid.NewGuid(), "user/42").Should().NotBe(original);
        TenantProvisionedGrant.ParticipationRefFor(tenant, "user/43").Should().NotBe(original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_provisioning_witness_reaches_issuer_and_failure_cannot_report_clean_success(bool issueFails)
    {
        var tenant = Guid.NewGuid(); var user = Guid.NewGuid();
        var users = new Mock<IUserStore>();
        users.Setup(item => item.GetByTenantAndExternalUserAsync(tenant, "child", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IUser>.Success(new User { Id = user, OwnerTenantId = tenant, ExternalUserId = "child" }));
        var issuer = new Mock<ITenantProvisionedGrantIssuer>();
        var authority = ConsentFixture.Authority(tenant);
        issuer.Setup(item => item.IssueOnProvisionAsync(user, "child", authority, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueFails ? AZOAResult<ConsentLifecycleReceipt>.Failure("authority changed")
                : AZOAResult<ConsentLifecycleReceipt>.Success(new(Guid.NewGuid(), Guid.NewGuid(), user, tenant,
                    [AzoaScopes.NftMint], GrantOrigin.Participation, "fixture", DateTimeOffset.UtcNow, null)));
        var manager = new TenantManager(users.Object, new ConfigurationBuilder().Build(), Mock.Of<IConsentGrantStore>(), issuer.Object);
        var result = await manager.ProvisionChildAsync(tenant, new ProvisionChildModel { ExternalUserId = "child" }, authority);
        result.IsError.Should().Be(issueFails);
        issuer.Verify(item => item.IssueOnProvisionAsync(user, "child", authority, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Foreign_tenant_witness_cannot_start_provisioning()
    {
        var users = new Mock<IUserStore>(MockBehavior.Strict);
        var issuer = new Mock<ITenantProvisionedGrantIssuer>(MockBehavior.Strict);
        var manager = new TenantManager(users.Object, new ConfigurationBuilder().Build(), Mock.Of<IConsentGrantStore>(), issuer.Object);
        var result = await manager.ProvisionChildAsync(Guid.NewGuid(), new ProvisionChildModel { ExternalUserId = "child" },
            ConsentFixture.Authority(Guid.NewGuid()));
        result.IsError.Should().BeTrue();
        users.VerifyNoOtherCalls(); issuer.VerifyNoOtherCalls();
    }
}
