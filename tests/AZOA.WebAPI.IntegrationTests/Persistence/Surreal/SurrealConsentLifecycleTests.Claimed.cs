using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Consent;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentWebhookEvent;
using Grant = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;
using User = AZOA.WebAPI.Persistence.SurrealDb.Models.User;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Claimed-member custody access and tenant relinquish; see Providers/Stores/Surreal/AGENTS.md §claimed-custodial-fence.</summary>
public sealed partial class SurrealConsentLifecycleTests
{
    private const string ClaimedExternal = "claimed-child";

    [Theory]
    [InlineData("revoked")]
    [InlineData("watermark-moved")]
    [InlineData("tenant-revoked")]
    public async Task Claimed_access_rechecks_the_exact_user_explicit_grant_version_in_the_consuming_transaction(string change)
    {
        var (member, grant, credential) = await ClaimedMemberWithGrantAsync([AzoaScopes.KycRead, AzoaScopes.TransferSign]);
        var service = new TenantCustodialAccess(_executor);
        var authority = TenantAuthority([AzoaScopes.TenantProvision, AzoaScopes.KycRead]);
        var captured = await service.CaptureAsync(member, ClaimedExternal, authority, AzoaScopes.KycRead);
        captured.Should().NotBeNull("the claimed member's own live grant covers kyc:read");
        captured!.Kind.Should().Be(TenantCustodialKind.Claimed);
        captured.MembershipId.Should().BeNull();
        captured.GrantId.Should().EndWith(grant.GrantId.ToString("N"));
        (await service.ValidateAsync(captured, AzoaScopes.KycRead)).Should().BeTrue();

        if (change == "revoked") (await _store.RevokeUserExplicitAsync(grant.GrantId, credential)).Should().NotBeNull();
        else await StampAuthNotBeforeAsync(change == "watermark-moved" ? member : _tenant);

        (await service.ValidateAsync(captured, AzoaScopes.KycRead)).Should().BeFalse();
    }

    [Fact]
    public async Task Claimed_capture_needs_this_tenants_deterministic_member_and_a_grant_covering_the_scope()
    {
        var (member, _, credential) = await ClaimedMemberWithGrantAsync([AzoaScopes.TransferSign]);
        var service = new TenantCustodialAccess(_executor);

        (await service.CaptureAsync(member, ClaimedExternal, TenantAuthority([AzoaScopes.TenantProvision, AzoaScopes.KycRead]),
            AzoaScopes.KycRead)).Should().BeNull("the person's grant does not cover kyc:read");
        (await service.ResolveOwnershipAsync(member, ClaimedExternal, _tenant)).Should().Be(TenantCustodialKind.Claimed);

        // The person also consents to a second tenant; that tenant still cannot reach the account through custody,
        // because the account is not the second tenant's deterministic member for the same external user.
        var otherTenant = Guid.NewGuid();
        await CreateUserAsync(new User { Id = otherTenant.ToString("N"), Username = otherTenant.ToString("N"),
            Email = otherTenant + "@test.invalid", PasswordHash = "test", IsActive = true });
        (await _store.AdmitUserExplicitAsync(new ConsentLifecycleCommand(Guid.NewGuid(), otherTenant, [AzoaScopes.KycRead],
            GrantOrigin.UserExplicit, null, null), credential)).Should().NotBeNull();
        (await service.ResolveOwnershipAsync(member, ClaimedExternal, otherTenant)).Should().BeNull();
        (await service.CaptureAsync(member, ClaimedExternal, new SigningConsentWitness(1, Authority() with { ActorUserId = otherTenant },
            [AzoaScopes.TenantProvision, AzoaScopes.KycRead], []), AzoaScopes.KycRead)).Should().BeNull();
    }

    [Fact]
    public async Task Relinquish_revokes_every_live_grant_to_this_tenant_with_one_revoked_event_each_and_spares_other_tenants()
    {
        var (member, first, credential) = await ClaimedMemberWithGrantAsync([AzoaScopes.KycRead]);
        var second = (await _store.AdmitUserExplicitAsync(new ConsentLifecycleCommand(Guid.NewGuid(), _tenant,
            [AzoaScopes.TransferSign], GrantOrigin.UserExplicit, null, null), credential))!;
        var otherTenant = Guid.NewGuid();
        await CreateUserAsync(new User { Id = otherTenant.ToString("N"), Username = otherTenant.ToString("N"),
            Email = otherTenant + "@test.invalid", PasswordHash = "test", IsActive = true });
        var elsewhere = (await _store.AdmitUserExplicitAsync(new ConsentLifecycleCommand(Guid.NewGuid(), otherTenant,
            [AzoaScopes.TransferSign], GrantOrigin.UserExplicit, null, null), credential))!;

        var relinquished = await _store.RelinquishAsync(_tenant, ClaimedExternal, TenantAuthority([AzoaScopes.TenantProvision]));

        relinquished.IsError.Should().BeFalse(relinquished.Message);
        relinquished.Result!.RevokedGrantIds.Should().BeEquivalentTo([first.GrantId, second.GrantId]);
        var grants = (await _executor.QueryAsync<Grant>(SurrealQuery<Grant>.From())).ToDictionary(row => SurrealRecordGuid.Parse(row.Id));
        grants[first.GrantId].RevokedAt.Should().Be(relinquished.Result.RelinquishedAt);
        grants[second.GrantId].RevokedAt.Should().Be(relinquished.Result.RelinquishedAt);
        grants[elsewhere.GrantId].RevokedAt.Should().BeNull("a tenant can only give up what it was given");
        var memberLink = SurrealLink.ToLink("user", member.ToString("N"));
        (await _executor.QueryAsync<Event>(SurrealQuery<Event>.From().Where(row => row.UserId == memberLink)))
            .Where(row => row.EventType == Event.WebhookEventType.Revoked).Select(row => SurrealRecordGuid.Parse(row.GrantId))
            .Should().BeEquivalentTo([first.GrantId, second.GrantId]);

        var again = await _store.RelinquishAsync(_tenant, ClaimedExternal, TenantAuthority([AzoaScopes.TenantProvision]));
        again.Result!.RevokedCount.Should().Be(0);
        (await _store.RelinquishAsync(_tenant, "not-a-member", TenantAuthority([AzoaScopes.TenantProvision])))
            .Code.Should().Be(AzoaErrorCodes.NotFound);
    }

    /// <summary>A claimed member exactly as the claim leaves it: deterministic id, unlinked, watermark set, then its own grant.</summary>
    private async Task<(Guid Member, ConsentLifecycleReceipt Grant, SigningCredentialWitness Credential)> ClaimedMemberWithGrantAsync(
        IReadOnlyList<string> scopes)
    {
        var member = TenantChildCorrelation.DeterministicUserId(_tenant, ClaimedExternal);
        var watermark = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeSeconds());
        await CreateUserAsync(new User { Id = member.ToString("N"), Username = member.ToString("N"), Email = member + "@test.invalid",
            PasswordHash = "test", IsActive = true, ExternalUserId = ClaimedExternal, AuthNotBefore = watermark });
        var credential = Authority() with { ActorUserId = member, AuthNotBefore = watermark };
        var grant = await _store.AdmitUserExplicitAsync(new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, scopes,
            GrantOrigin.UserExplicit, null, null), credential);
        grant.Should().NotBeNull();
        return (member, grant!, credential);
    }

    private SigningConsentWitness TenantAuthority(IReadOnlyList<string> scopes)
        => new(1, Authority() with { ActorUserId = _tenant }, scopes, []);

    private async Task CreateUserAsync(User user) => (await _executor.ExecuteAsync(SurrealWriter.Create(user))).EnsureAllOk();

    private async Task StampAuthNotBeforeAsync(Guid user)
        => (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(user.ToString("N")).Where(seed => true)
            .Set(row => row.AuthNotBefore, DateTimeOffset.UtcNow).Build())).EnsureAllOk();
}
