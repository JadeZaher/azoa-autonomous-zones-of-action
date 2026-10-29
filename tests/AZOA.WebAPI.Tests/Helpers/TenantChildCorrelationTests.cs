using AZOA.WebAPI.Models;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Helpers;

public sealed class TenantChildCorrelationTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid OtherTenant = Guid.Parse("99999999-2222-3333-4444-555555555555");

    [Fact]
    public void The_v1_deterministic_id_is_pinned_because_changing_it_is_an_identity_migration()
        => TenantChildCorrelation.DeterministicUserId(Tenant, "member-1")
            .Should().Be(Guid.Parse("10286c16-32f6-1689-2c56-cdfc6d5a7050"));

    public static TheoryData<string, Guid, Guid?, string?, Guid, bool> Cases()
    {
        var member = TenantChildCorrelation.DeterministicUserId(Tenant, "member-1");
        return new()
        {
            { "tenant-linked", Guid.NewGuid(), Tenant, "member-1", Tenant, true },
            { "linked to another tenant", member, OtherTenant, "member-1", Tenant, false },
            { "claimed with the tenant's deterministic id", member, null, "member-1", Tenant, true },
            { "claimed, asked by another tenant", member, null, "member-1", OtherTenant, false },
            { "claimed but the id belongs to another external user", member, null, "member-2", Tenant, false },
            { "claimed with a non-deterministic id", Guid.NewGuid(), null, "member-1", Tenant, false },
            { "self-registered account with no external user", member, null, null, Tenant, false },
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_member_is_correlated_while_linked_or_after_claiming_the_deterministic_account(
        string because, Guid userId, Guid? owner, string? external, Guid askingTenant, bool correlated)
    {
        var user = new User { Id = userId, OwnerTenantId = owner, ExternalUserId = external };
        TenantChildCorrelation.IsCorrelated(user, askingTenant).Should().Be(correlated, because);
    }
}
