using System.Net;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using SurrealForge.Client.Query;
using ClaimInviteRow = AZOA.WebAPI.Persistence.SurrealDb.Models.WalletAuthClaimToken;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>
/// Tenant mints, member redeems. This host returns the raw token as a test fixture
/// (<c>appsettings.IntegrationTest.json</c>) so the member side can be driven; see <c>Http/AGENTS.md</c> §claim-invite.
/// </summary>
public sealed class ClaimInviteFlowTests(HttpFlowWebApplicationFactory factory) : ClaimInviteFlowTestBase(factory)
{
    [Fact]
    public async Task Member_claims_with_the_delivered_token_while_storage_holds_only_its_digest()
    {
        using var tenant = await OnboardTenantAsync();
        var member = await ProvisionMemberAsync(tenant);
        using var minted = await MintAsync(tenant, member);
        var token = (await ReadOkAsync<ClaimInviteResponse>(minted)).ClaimToken;
        token.Should().NotBeNullOrEmpty("the IntegrationTest host returns the token as a fixture");

        var stored = await InviteRowAsync(member);
        stored.Token.Should().Be(OpaqueTokenDigest.Compute(token!)).And.NotBe(token);

        // Whoever can read the invite table cannot redeem what it holds.
        using var leakedDigest = await ClaimAsync(member, stored.Token, "Leaked-digest-Pass1");
        await ShouldReturnAsync(leakedDigest, HttpStatusCode.NotFound);

        await WalletIdentityReadyAsync();
        using var claim = await ClaimAsync(member, token!, MemberPassword);
        (await ReadOkAsync<WalletAuthTokenResponse>(claim)).UserId.Should().Be(member);
        (await InviteRowAsync(member)).ConsumedAt.Should().NotBeNull();
        (await OwnerTenantAsync(member)).Should().BeNull("the claim severs tenant ownership");
    }

    [Fact]
    public async Task A_used_invite_cannot_reset_the_members_password()
    {
        using var tenant = await OnboardTenantAsync();
        var member = await ProvisionMemberAsync(tenant);
        using var minted = await MintAsync(tenant, member);
        var token = (await ReadOkAsync<ClaimInviteResponse>(minted)).ClaimToken!;
        await WalletIdentityReadyAsync();
        using var claim = await ClaimAsync(member, token, MemberPassword);
        await ReadOkAsync<WalletAuthTokenResponse>(claim);
        var ownerPassword = (await UserRowAsync(member)).PasswordHash;

        using var replay = await ClaimAsync(member, token, "Replayed-link-Pass1");

        await ShouldReturnAsync(replay, HttpStatusCode.NotFound);
        (await UserRowAsync(member)).PasswordHash.Should().Be(ownerPassword);
    }

    [Fact]
    public async Task An_expired_invite_is_refused_and_the_member_stays_with_the_tenant()
    {
        using var tenant = await OnboardTenantAsync();
        var member = await ProvisionMemberAsync(tenant);
        using var minted = await MintAsync(tenant, member);
        var token = (await ReadOkAsync<ClaimInviteResponse>(minted)).ClaimToken!;
        // No route can age an invite; move its expiry into the past the way the clock eventually would.
        await WriteAsync(SurrealWriter.UpdateOnly<ClaimInviteRow>((await InviteRowAsync(member)).Id).Where(seed => true)
            .Set(row => row.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)).Build());
        await WalletIdentityReadyAsync();

        using var claim = await ClaimAsync(member, token, MemberPassword);

        await ShouldReturnAsync(claim, HttpStatusCode.NotFound);
        (await InviteRowAsync(member)).ConsumedAt.Should().BeNull();
        (await OwnerTenantAsync(member)).Should().Be(tenant.TenantId);
    }

    [Fact]
    public async Task A_token_matching_a_legacy_plaintext_invite_is_refused()
    {
        using var tenant = await OnboardTenantAsync();
        var member = await ProvisionMemberAsync(tenant);
        var legacyToken = Crypto.GenerateNonce();
        // A row minted before hashing stored the raw token itself; no route can create one now.
        await WriteAsync(SurrealWriter.Create(new ClaimInviteRow
        {
            Id = Guid.NewGuid().ToString("N"),
            Token = legacyToken,
            TargetUserId = UserLink(member),
            TenantId = UserLink(tenant.TenantId),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            CreatedAt = DateTimeOffset.UtcNow,
        }));
        await WalletIdentityReadyAsync();

        using var claim = await ClaimAsync(member, legacyToken, "Legacy-link-Pass1");

        await ShouldReturnAsync(claim, HttpStatusCode.NotFound);
        (await InviteRowAsync(member)).ConsumedAt.Should().BeNull();
        (await OwnerTenantAsync(member)).Should().Be(tenant.TenantId);
    }
}
