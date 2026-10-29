using System.Net;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>
/// The production claim-invite policy: the tenant that mints an invite never receives the token that
/// would let it claim the member's account itself. See <c>Http/AGENTS.md</c> §claim-invite.
/// </summary>
public sealed class ClaimInviteWithheldTokenFlowTests(WithheldClaimTokenWebApplicationFactory factory)
    : ClaimInviteFlowTestBase(factory), IClassFixture<WithheldClaimTokenWebApplicationFactory>
{
    [Fact]
    public async Task A_tenant_mints_a_three_day_invite_without_ever_seeing_its_token()
    {
        using var tenant = await OnboardTenantAsync();
        var member = await ProvisionMemberAsync(tenant);
        var mintedAt = DateTime.UtcNow;

        using var minted = await MintAsync(tenant, member);

        await ShouldReturnAsync(minted, HttpStatusCode.OK);
        var invite = await ResultElementAsync(minted);
        invite.TryGetProperty("claimToken", out _).Should().BeFalse(invite.GetRawText());
        invite.GetProperty("targetUserId").GetGuid().Should().Be(member);
        invite.GetProperty("expiresAt").GetDateTime().Should().BeCloseTo(mintedAt.AddHours(72), TimeSpan.FromMinutes(1));
        OpaqueTokenDigest.IsV1Digest((await InviteRowAsync(member)).Token).Should().BeTrue("only the token's digest is stored");
    }
}
