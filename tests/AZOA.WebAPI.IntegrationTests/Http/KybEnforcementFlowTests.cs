using System.Net;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyb;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>
/// D-6 KYB enforcement (round-4 L2): custodial access, tenant-on-behalf signing and payout price
/// schedules each require the tenant's current business verification. Allow and deny for all three.
/// See <c>Http/AGENTS.md</c> §flows and <c>Services/Kyb/AGENTS.md</c> §KYB gate.
/// </summary>
public sealed class KybEnforcementFlowTests(HttpFlowWebApplicationFactory factory) : HttpFlowTestBase(factory)
{
    [Fact]
    public async Task Custodial_accounts_require_a_current_business_verification()
    {
        using var tenant = await OnboardTenantAsync();
        await AssignIdentomatToTenantAsync(tenant.TenantId);
        var subject = "member-" + Guid.NewGuid().ToString("N")[..12];

        using var refused = await EnrolAsync(tenant, subject);
        await ShouldReturnAsync(refused, HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync()).Should().Contain(KybGate.RequiredCode);

        await TestDataBuilders.KybApprovedTenant(tenant.TenantId).SeedAsync(Factory.Services);
        using var enrolled = await EnrolAsync(tenant, subject);
        (await ReadOkAsync<TenantCustodialAccountStatusResponse>(enrolled)).IdentityReady.Should().BeTrue();

        // A lapsed verification closes custodial KYC again.
        await ExpireKybAsync(tenant.TenantId);
        var begin = new HttpRequestMessage(HttpMethod.Post, $"api/tenant/custodial-accounts/{subject}/kyc/session");
        begin.Headers.Add("Idempotency-Key", "kyc-" + subject);
        using var lapsed = await tenant.Client.SendAsync(begin);
        await ShouldReturnAsync(lapsed, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Tenant_driven_signing_on_a_members_behalf_requires_a_current_business_verification()
    {
        using var tenant = await OnboardTenantAsync();
        await AssignIdentomatToTenantAsync(tenant.TenantId);
        await TestDataBuilders.KybApprovedTenant(tenant.TenantId).SeedAsync(Factory.Services);
        var subject = "member-" + Guid.NewGuid().ToString("N")[..12];
        var member = await ReadOkAsync<TenantCustodialAccountStatusResponse>(await EnrolAsync(tenant, subject));
        var signing = SigningContext.ForUser(Guid.Parse(member.UserId!), Guid.NewGuid())
            .ActingAs(tenant.TenantId, AzoaScopes.NftMint, Guid.Parse(member.UserId!));

        (await ConsentAsync(signing)).IsError.Should().BeFalse("the enrolment grant covers nft:mint and KYB is current");

        await ExpireKybAsync(tenant.TenantId);
        var denied = await ConsentAsync(signing);
        denied.IsError.Should().BeTrue();
        denied.Message.Should().StartWith(KybGate.RequiredCode);
    }

    [Theory]
    [InlineData("approved", true)]
    [InlineData("absent", false)]
    [InlineData("expired", false)]
    [InlineData("rejected", false)]
    [InlineData("stale-authority", false)]
    public async Task Payout_price_schedules_are_gated_on_the_tenants_current_verification(string state, bool allowed)
    {
        var tenantId = (await SeedUserAsync()).Id;
        if (state != "absent")
            await TestDataBuilders.KybApprovedTenant(tenantId).SeedAsync(Factory.Services);
        var row = tenantId.ToString("N");
        switch (state)
        {
            case "expired": await ExpireKybAsync(tenantId); break;
            case "rejected": await WriteAsync(SurrealWriter.UpdateOnly<KybVerification>(row).Where(seed => true).Set(value => value.Status, "REJECTED").Build()); break;
            case "stale-authority": await WriteAsync(SurrealWriter.UpdateOnly<KybVerification>(row).Where(seed => true).Set(value => value.AuthorityRevision, "previous-config").Build()); break;
        }

        using var scope = Factory.Services.CreateScope();
        var decision = await scope.ServiceProvider.GetRequiredService<IKybGate>()
            .RequireVerifiedTenantAsync(tenantId, KybCapability.PayoutPriceSchedule);

        decision.IsError.Should().Be(!allowed);
        if (!allowed) decision.Code.Should().Be(KybGate.RequiredCode);
    }

    private static Task<HttpResponseMessage> EnrolAsync(FlowTenant tenant, string subject)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"api/tenant/custodial-accounts/{subject}");
        request.Headers.Add("Idempotency-Key", "enrol-" + subject);
        return tenant.Client.SendAsync(request);
    }

    private Task ExpireKybAsync(Guid tenantId)
        => WriteAsync(SurrealWriter.UpdateOnly<KybVerification>(tenantId.ToString("N")).Where(seed => true)
            .Set(value => value.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)).Build());

    private async Task<AZOAResult<bool>> ConsentAsync(SigningContext signing)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITenantConsentGate>().EnsureAllowedAsync(signing);
    }
}
