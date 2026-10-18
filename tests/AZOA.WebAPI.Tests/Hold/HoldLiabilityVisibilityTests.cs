using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Hold;

/// <summary>The liability visibility rule (Services/Hold/AGENTS.md §liability-reads), table-driven.</summary>
public sealed class HoldLiabilityVisibilityTests
{
    private static readonly Guid Actor = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly Guid Other = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly Guid Tenant = new("cccccccccccccccccccccccccccccccc");
    private static readonly Guid OtherTenant = new("dddddddddddddddddddddddddddddddd");

    public static TheoryData<string, Guid, Guid, Guid?, Guid?, bool> Cases => new()
    {
        // case, owner, beneficiary, custody tenant, acting tenant, visible to Actor
        { "own untenanted, login", Actor, Actor, null, null, true },
        { "received untenanted, login", Other, Actor, null, null, true },
        { "custody held by actor-as-tenant, login", Other, Other, Actor, null, true },
        { "owed to actor under a tenant, login", Other, Actor, Tenant, null, true },
        { "sent by actor under a tenant, login", Actor, Other, Tenant, null, false },
        { "sent by actor under a tenant, that tenant", Actor, Other, Tenant, Tenant, true },
        { "owed to actor under a tenant, that tenant", Other, Actor, Tenant, Tenant, true },
        { "sent by actor under another tenant, acting tenant", Actor, Other, OtherTenant, Tenant, false },
        { "own untenanted, under a tenant credential", Actor, Actor, null, Tenant, false },
        { "foreign", Other, Other, null, null, false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_liability_is_visible_exactly_when_owed_to_the_caller_or_scoped_to_its_credential(
        string name, Guid owner, Guid beneficiary, Guid? custody, Guid? acting, bool expected)
    {
        var row = new HoldLedger
        {
            Id = "hold", HoldId = "hold", OwnerUserId = owner.ToString("N"),
            BeneficiaryUserId = "user:" + beneficiary.ToString("N"),
            TenantId = custody is { } tenant ? "user:" + tenant.ToString("N") : null,
        };

        HoldLiabilityReadService.Visible(row, Actor, acting).Should().Be(expected, name);
    }
}
