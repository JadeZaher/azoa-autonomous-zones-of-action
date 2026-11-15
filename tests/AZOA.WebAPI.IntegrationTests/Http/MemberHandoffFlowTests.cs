using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using FluentAssertions;
using SurrealForge.Client.Query;
using Grant = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;
using Handoff = AZOA.WebAPI.Persistence.SurrealDb.Models.MemberHandoff;

namespace AZOA.WebAPI.IntegrationTests.Http;

public sealed class MemberHandoffFlowTests(MemberHandoffWebApplicationFactory factory)
    : ClaimInviteFlowTestBase(factory), IClassFixture<MemberHandoffWebApplicationFactory>
{
    private static readonly MemberHandoffRequest Request = new("member@example.test", "https://app.example/portfolio/withdraw");

    [Fact]
    public async Task Claim_then_explicit_permission_is_target_bound_and_replays_one_exact_grant()
    {
        await WalletIdentityReadyAsync();
        using var tenant = await OnboardTenantAsync();
        using var other = await OnboardTenantAsync();
        var member = await ProvisionMemberAsync(tenant);
        var subject = (await UserRowAsync(member)).ExternalUserId!;
        var route = $"api/tenant/custodial-accounts/{subject}/handoff";
        using var issued = await tenant.Client.PostAsJsonAsync(route, Request, JsonOptions);
        var receipt = await ReadOkAsync<MemberHandoffReceipt>(issued);
        var wire = await issued.Content.ReadAsStringAsync();
        wire.Should().NotContain("claimToken").And.NotContain("#token").And.NotContain("member@example");
        await ShouldReturnAsync(await other.Client.GetAsync(route + "/" + receipt.RequestId), HttpStatusCode.NotFound);
        var tenantDelivery = await tenant.Client.GetAsync("api/operator/member-handoff-deliveries/" + receipt.RequestId);
        tenantDelivery.IsSuccessStatusCode.Should().BeFalse();
        var delivery = await ReadOkAsync<MemberHandoffDelivery>(await Operator.GetAsync("api/operator/member-handoff-deliveries/" + receipt.RequestId));
        var token = Token(delivery);
        var saved = (await RowsAsync(SurrealQuery<Handoff>.Key(receipt.RequestId.ToString("N")))).Single();
        saved.ProtectedDelivery.Should().NotContain(token).And.NotContain(Request.DeliveryEmail);
        saved.TokenDigest.Should().NotBe(token);
        var preview = await ReadOkAsync<MemberHandoffView>(await Anonymous.PostAsJsonAsync("api/user/handoffs/preview", new MemberHandoffPreview(receipt.RequestId, token), JsonOptions));
        preview.Scopes.Should().BeEquivalentTo(new[] { "kyc:read", "kyc:submit", "units:issue", "swap:sign", "transfer:sign" });
        var tenantApproval = await tenant.Client.PostAsJsonAsync($"api/user/handoffs/{receipt.RequestId}/approve", new { });
        tenantApproval.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        var login = await ReadOkAsync<WalletAuthTokenResponse>(await ClaimAsync(member, token, MemberPassword));
        using var person = Flow.CreateClient();
        person.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var approved = await ReadOkAsync<MemberHandoffView>(await person.PostAsJsonAsync($"api/user/handoffs/{receipt.RequestId}/approve", new { }));
        approved.Status.Should().Be("approved");
        var discardRoute = $"api/fiat/payouts/{member}/requests/lost-firstparty-answer/void";
        var discarded = await ReadOkAsync<CashOutVoidView>(await person.PostAsync(discardRoute, null));
        discarded.Outcome.Should().Be("voided");
        discarded.CashOut.Should().BeNull();
        (await ReadOkAsync<CashOutVoidView>(await person.PostAsync(discardRoute, null))).Outcome.Should().Be("voided");
        await TestDataBuilders.KybApprovedTenant(tenant.TenantId).SeedAsync(Factory.Services);
        using var tenantDiscard = await tenant.Client.PostAsync(discardRoute, null);
        await ShouldReturnAsync(tenantDiscard, HttpStatusCode.Conflict);
        (await tenantDiscard.Content.ReadFromJsonAsync<AZOAResult<CashOutVoidView>>(JsonOptions))!.Code.Should().Be("REQUEST_KEY_IN_USE");

        await ReadOkAsync<MemberHandoffView>(await person.PostAsJsonAsync($"api/user/handoffs/{receipt.RequestId}/approve", new { }));
        await ShouldReturnAsync(await person.PostAsJsonAsync($"api/user/handoffs/{receipt.RequestId}/decline", new { }), HttpStatusCode.Conflict);
        var grants = await RowsAsync(SurrealQuery<Grant>.From().Where(row => row.GrantorUserId == UserLink(member) && row.RevokedAt == null));
        grants.Should().ContainSingle();
        grants.Single().Origin.Should().Be("UserExplicit");
        grants.Single().Scopes.Split(',').Should().BeEquivalentTo(preview.Scopes);
        await ShouldReturnAsync(await Anonymous.PostAsJsonAsync("api/user/handoffs/preview", new MemberHandoffPreview(receipt.RequestId, token), JsonOptions), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replacement_retires_the_old_claim_link_and_altered_resend_terms_are_refused()
    {
        await WalletIdentityReadyAsync();
        using var tenant = await OnboardTenantAsync();
        var member = await ProvisionMemberAsync(tenant);
        var subject = (await UserRowAsync(member)).ExternalUserId!;
        var route = $"api/tenant/custodial-accounts/{subject}/handoff";
        var first = await ReadOkAsync<MemberHandoffReceipt>(await tenant.Client.PostAsJsonAsync(route, Request, JsonOptions));
        var oldToken = Token(await ReadOkAsync<MemberHandoffDelivery>(await Operator.GetAsync("api/operator/member-handoff-deliveries/" + first.RequestId)));
        await ShouldReturnAsync(await tenant.Client.PostAsJsonAsync(route + $"/{first.RequestId}/resend", Request with { ReturnUrl = "https://evil.example/portfolio/withdraw" }, JsonOptions), HttpStatusCode.BadRequest);
        var second = await ReadOkAsync<MemberHandoffReceipt>(await tenant.Client.PostAsJsonAsync(route, Request, JsonOptions));
        var newToken = Token(await ReadOkAsync<MemberHandoffDelivery>(await Operator.GetAsync("api/operator/member-handoff-deliveries/" + second.RequestId)));
        var oldClaim = await ClaimAsync(member, oldToken, MemberPassword);
        oldClaim.IsSuccessStatusCode.Should().BeFalse();
        await ReadOkAsync<WalletAuthTokenResponse>(await ClaimAsync(member, newToken, MemberPassword));
    }

    [Fact]
    public async Task Approve_and_decline_race_has_one_decision_and_never_grants_after_decline()
    {
        await WalletIdentityReadyAsync();
        using var tenant = await OnboardTenantAsync();
        var member = await ProvisionMemberAsync(tenant);
        var subject = (await UserRowAsync(member)).ExternalUserId!;
        var receipt = await ReadOkAsync<MemberHandoffReceipt>(await tenant.Client.PostAsJsonAsync($"api/tenant/custodial-accounts/{subject}/handoff", Request, JsonOptions));
        var token = Token(await ReadOkAsync<MemberHandoffDelivery>(await Operator.GetAsync("api/operator/member-handoff-deliveries/" + receipt.RequestId)));
        var login = await ReadOkAsync<WalletAuthTokenResponse>(await ClaimAsync(member, token, MemberPassword));
        using var person = Flow.CreateClient();
        person.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        await Task.WhenAll(person.PostAsJsonAsync($"api/user/handoffs/{receipt.RequestId}/approve", new { }),
            person.PostAsJsonAsync($"api/user/handoffs/{receipt.RequestId}/decline", new { }));
        var final = await ReadOkAsync<MemberHandoffView>(await person.GetAsync($"api/user/handoffs/{receipt.RequestId}"));
        final.Status.Should().BeOneOf("approved", "approving", "declined");
        var grants = await RowsAsync(SurrealQuery<Grant>.From().Where(row => row.GrantorUserId == UserLink(member) && row.RevokedAt == null));
        if (final.Status == "declined") grants.Should().BeEmpty();
        else grants.Count.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Claimed_member_gets_a_tokenless_consent_renewal_and_approves_its_immutable_terms()
    {
        await WalletIdentityReadyAsync();
        using var tenant = await OnboardTenantAsync();
        var member = await ProvisionMemberAsync(tenant);
        var subject = (await UserRowAsync(member)).ExternalUserId!;
        var route = $"api/tenant/custodial-accounts/{subject}/handoff";
        var initial = await ReadOkAsync<MemberHandoffReceipt>(await tenant.Client.PostAsJsonAsync(route, Request, JsonOptions));
        var token = Token(await ReadOkAsync<MemberHandoffDelivery>(await Operator.GetAsync("api/operator/member-handoff-deliveries/" + initial.RequestId)));
        var login = await ReadOkAsync<WalletAuthTokenResponse>(await ClaimAsync(member, token, MemberPassword));
        var claimed = await UserRowAsync(member);
        claimed.OwnerTenantId.Should().BeNull();
        claimed.AuthNotBefore.Should().NotBeNull();
        using var issued = await tenant.Client.PostAsJsonAsync(route, Request, JsonOptions);
        var renewal = await ReadOkAsync<MemberHandoffReceipt>(issued);
        renewal.Ownership.Should().Be("claimed");
        renewal.TargetUserId.Should().Be(member);
        renewal.RequestId.Should().NotBe(initial.RequestId);
        (await issued.Content.ReadAsStringAsync()).Should().NotContain("claimToken").And.NotContain("token=");
        var delivery = await ReadOkAsync<MemberHandoffDelivery>(await Operator.GetAsync("api/operator/member-handoff-deliveries/" + renewal.RequestId));
        delivery.Text.Should().Contain("/consent#").And.NotContain("/claim#").And.NotContain("&token=");
        var stored = (await RowsAsync(SurrealQuery<Handoff>.Key(renewal.RequestId.ToString("N")))).Single();
        stored.TokenDigest.Should().BeNull();
        using var person = Flow.CreateClient();
        person.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var descriptor = await ReadOkAsync<MemberHandoffView>(await person.GetAsync($"api/user/handoffs/{renewal.RequestId}"));
        descriptor.Ownership.Should().Be("claimed");
        descriptor.ReturnUrl.Should().Be(Request.ReturnUrl);
        var approved = await ReadOkAsync<MemberHandoffView>(await person.PostAsJsonAsync($"api/user/handoffs/{renewal.RequestId}/approve", new { }));
        approved.Status.Should().Be("approved");
        approved.Scopes.Should().BeEquivalentTo(descriptor.Scopes);
        approved.GrantExpiresAt.Should().Be(descriptor.GrantExpiresAt);
        approved.RequestId.Should().Be(renewal.RequestId);
        var grants = await RowsAsync(SurrealQuery<Grant>.From().Where(row => row.GrantorUserId == UserLink(member) && row.RevokedAt == null));
        grants.Should().ContainSingle();
        grants.Single().TenantId.Should().Be(UserLink(tenant.TenantId));
        grants.Single().ExpiresAt.Should().Be(descriptor.GrantExpiresAt);
        grants.Single().Scopes.Split(',').Should().BeEquivalentTo(descriptor.Scopes);
    }

    [Fact]
    public async Task Expired_approving_request_gives_typed_fresh_request_guidance_and_can_be_resolved_by_decline()
    {
        await WalletIdentityReadyAsync();
        using var tenant = await OnboardTenantAsync();
        var member = await ProvisionMemberAsync(tenant);
        var subject = (await UserRowAsync(member)).ExternalUserId!;
        var receipt = await ReadOkAsync<MemberHandoffReceipt>(await tenant.Client.PostAsJsonAsync($"api/tenant/custodial-accounts/{subject}/handoff", Request, JsonOptions));
        var token = Token(await ReadOkAsync<MemberHandoffDelivery>(await Operator.GetAsync("api/operator/member-handoff-deliveries/" + receipt.RequestId)));
        var login = await ReadOkAsync<WalletAuthTokenResponse>(await ClaimAsync(member, token, MemberPassword));
        using var person = Flow.CreateClient();
        person.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var saved = (await RowsAsync(SurrealQuery<Handoff>.Key(receipt.RequestId.ToString("N")))).Single();
        var descriptor = JsonSerializer.Deserialize<MemberHandoffDescriptor>(saved.Descriptor, JsonOptions)!;
        var expired = DateTimeOffset.UtcNow.AddMinutes(-1);
        await WriteAsync(SurrealWriter.UpdateOnly<Handoff>(receipt.RequestId.ToString("N"))
            .Where(row => row.Status == "pending" && row.Version == saved.Version)
            .Set(row => row.Status, "approving").Set(row => row.ExpiresAt, expired)
            .Set(row => row.Descriptor, JsonSerializer.Serialize(descriptor with { ExpiresAt = expired }, JsonOptions)).Build());
        using var answer = await person.PostAsJsonAsync($"api/user/handoffs/{receipt.RequestId}/approve", new { });
        await ShouldReturnAsync(answer, HttpStatusCode.Conflict);
        (await answer.Content.ReadFromJsonAsync<AZOAResult<MemberHandoffView>>(JsonOptions))!.Code.Should().Be("HANDOFF_EXPIRED");
        var declined = await ReadOkAsync<MemberHandoffView>(await person.PostAsJsonAsync($"api/user/handoffs/{receipt.RequestId}/decline", new { }));
        declined.Status.Should().Be("declined");
        (await RowsAsync(SurrealQuery<Grant>.From().Where(row => row.GrantorUserId == UserLink(member) && row.RevokedAt == null))).Should().BeEmpty();
    }

    private static string Token(MemberHandoffDelivery delivery)
    {
        var url = new Uri(delivery.Text.Split('\n').Last());
        return Uri.UnescapeDataString(url.Fragment.TrimStart('#').Split('&').Single(part => part.StartsWith("token=", StringComparison.Ordinal))[6..]);
    }
}
