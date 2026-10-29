using System.Net.Http.Json;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Auth;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using ClaimInviteRow = AZOA.WebAPI.Persistence.SurrealDb.Models.WalletAuthClaimToken;
using UserRow = AZOA.WebAPI.Persistence.SurrealDb.Models.User;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

/// <summary>Tenant → member claim-invite routes and the rows they leave; see <c>Http/AGENTS.md</c> §claim-invite.</summary>
public abstract class ClaimInviteFlowTestBase(HttpFlowWebApplicationFactory factory) : HttpFlowTestBase(factory)
{
    protected const string MemberPassword = "Member-chosen-Pass1";

    /// <summary>The tenant provisions an unclaimed member it owns (<c>POST api/tenant/users</c>).</summary>
    protected async Task<Guid> ProvisionMemberAsync(FlowTenant tenant)
    {
        using var response = await tenant.Client.PostAsJsonAsync("api/tenant/users",
            new ProvisionChildModel { ExternalUserId = "claim-" + Guid.NewGuid().ToString("N") }, JsonOptions);
        return (await ReadOkAsync<ChildUserResponse>(response)).UserId;
    }

    protected Task<HttpResponseMessage> MintAsync(FlowTenant tenant, Guid member)
        => tenant.Client.PostAsJsonAsync("api/user/claim-invite", new CreateClaimInviteRequest { ChildUserId = member }, JsonOptions);

    /// <summary>The member redeems an invite anonymously with a password of their choosing.</summary>
    protected Task<HttpResponseMessage> ClaimAsync(Guid member, string claimToken, string newPassword)
        => Anonymous.PostAsJsonAsync("api/user/claim",
            new ClaimUserRequest { TargetUserId = member, ClaimToken = claimToken, NewPassword = newPassword }, JsonOptions);

    protected async Task<ClaimInviteRow> InviteRowAsync(Guid member)
    {
        var target = SurrealLink.ToLink("user", member.ToString("N"));
        return (await RowsAsync(SurrealQuery<ClaimInviteRow>.From().Where(row => row.TargetUserId == target))).Single();
    }

    protected async Task<UserRow> UserRowAsync(Guid user)
        => (await RowsAsync(SurrealQuery<UserRow>.Key(user.ToString("N")))).Single();

    protected static string UserLink(Guid user) => SurrealLink.ToLink("user", user.ToString("N"))!;

    /// <summary>The member's owning tenant, or null once claimed.</summary>
    protected async Task<Guid?> OwnerTenantAsync(Guid member)
        => (await UserRowAsync(member)).OwnerTenantId is { } owner ? SurrealId.FromSurrealId(SurrealLink.FromLink(owner)!) : null;

    /// <summary>Claims write through the wallet-identity readiness gate; drive the host's own migration to Ready first.</summary>
    protected async Task WalletIdentityReadyAsync()
    {
        using var scope = Flow.Services.CreateScope();
        var migration = scope.ServiceProvider.GetRequiredService<WalletAuthIdentityMigrationService>();
        for (var attempt = 0; attempt < 20 && (await migration.GetAsync(default)).Status != "Ready"; attempt++)
        {
            await migration.SweepAsync(default);
            if ((await migration.GetAsync(default)).Status != "Ready") await Task.Delay(250);
        }
        (await migration.GetAsync(default)).Status.Should().Be("Ready");
    }
}
