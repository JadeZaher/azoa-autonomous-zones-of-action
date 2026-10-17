using System.Net;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>
/// Custodial identity verification (KYC): a tenant enrols a member, starts the hosted Identomat
/// session, and the member's KYC gate opens only from the vendor's authoritative result.
/// See <c>Http/AGENTS.md</c> §flows.
/// </summary>
public sealed class IdentityVerificationFlowTests(HttpFlowWebApplicationFactory factory) : HttpFlowTestBase(factory)
{
    private const string CallbackIngress = "api/kyc/callback/identomat";

    [Fact]
    public async Task The_kyc_gate_opens_only_after_the_vendor_result_approves_and_replay_changes_nothing()
    {
        var (tenant, subject, memberId) = await EnrolMemberAsync();
        using var _ = tenant;

        var started = await BeginKycAsync(tenant, subject, "kyc-session-" + subject);
        started.Provider.Should().Be("identomat");
        started.HostedVerification.Should().BeTrue();
        started.VerificationUrl.Should().StartWith($"https://{IdentomatVendor.WidgetHost}/");
        (await AccountAsync(tenant, subject)).KycStatus.Should().Be(TenantKycStatus.Pending);
        var session = Flow.Identomat.LatestIdentitySessionFor(memberId)!;

        // The vendor has not decided: a callback claiming approval leaves the gate closed.
        (await PostCallbackAsync(CallbackIngress, Callback(session, "APPROVED"))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await AccountAsync(tenant, subject)).KycStatus.Should().Be(TenantKycStatus.Pending);

        session.Verdict = IdentomatVerdict.Approved;
        (await PostCallbackAsync(CallbackIngress, Callback(session, "REJECTED"))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await AccountAsync(tenant, subject)).KycStatus.Should().Be(TenantKycStatus.Approved);

        var decided = await SubmissionAsync(memberId);
        decided.Status.Should().Be(KycStatus.APPROVED);
        (await PostCallbackAsync(CallbackIngress, Callback(session, "APPROVED"))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var afterReplay = await SubmissionAsync(memberId);
        afterReplay.Status.Should().Be(KycStatus.APPROVED);
        afterReplay.ModifiedDate.Should().Be(decided.ModifiedDate);
        afterReplay.ExpiresAt.Should().Be(decided.ExpiresAt);
    }

    [Fact]
    public async Task A_forged_callback_decides_nothing_where_the_genuine_one_approves()
    {
        var (tenant, subject, memberId) = await EnrolMemberAsync();
        using var _ = tenant;
        await BeginKycAsync(tenant, subject, "kyc-session-" + subject);
        var session = Flow.Identomat.LatestIdentitySessionFor(memberId)!;
        session.Verdict = IdentomatVerdict.Approved;

        using var forged = await PostCallbackAsync(CallbackIngress,
            IdentomatVendor.CallbackBody("forged-" + Guid.NewGuid().ToString("N"), session.Id, "APPROVED"));
        (await AccountAsync(tenant, subject)).KycStatus.Should().Be(TenantKycStatus.Pending);

        // Same vendor state, right key: only the key differed, so only the key stopped the forgery.
        using var genuine = await PostCallbackAsync(CallbackIngress, Callback(session, "APPROVED"));
        (await AccountAsync(tenant, subject)).KycStatus.Should().Be(TenantKycStatus.Approved);
        forged.StatusCode.Should().Be(genuine.StatusCode, "an anonymous caller must not learn whether its key was right");
    }

    [Fact]
    public async Task A_vendor_rejection_closes_the_gate()
    {
        var (tenant, subject, memberId) = await EnrolMemberAsync();
        using var _ = tenant;
        await BeginKycAsync(tenant, subject, "kyc-session-" + subject);
        var session = Flow.Identomat.LatestIdentitySessionFor(memberId)!;

        session.Verdict = IdentomatVerdict.Rejected;
        await PostCallbackAsync(CallbackIngress, Callback(session, "APPROVED"));
        (await AccountAsync(tenant, subject)).KycStatus.Should().Be(TenantKycStatus.Rejected);
    }

    [Fact]
    public async Task The_tenant_policy_payloads_keep_the_shape_the_frontend_fixtures_declare()
    {
        using var tenant = await OnboardTenantAsync();
        await AssignIdentomatToTenantAsync(tenant.TenantId);

        // The /kyc page reads these as the tenant owner's first-party login session.
        using var owner = Factory.CreateAuthenticatedClientForUser(tenant.TenantId);
        using var selection = await owner.GetAsync("api/tenant/kyc/provider");
        (await ReadOkAsync<TenantKycSelectionResponse>(selection)).ProviderKey.Should().Be("identomat");
        using var choices = await owner.GetAsync("api/tenant/kyc/providers");
        (await ReadOkAsync<List<TenantKycProviderChoiceResponse>>(choices))
            .Should().Contain(choice => choice.ProviderKey == "identomat");

        FrontendFixtureContract.ShouldMatchShape(await ResultElementAsync(selection),
            FrontendFixtureContract.Load("tenant-kyc.json", "selectedIdentomat"));
        var identomatChoice = (await ResultElementAsync(choices)).EnumerateArray()
            .Single(choice => choice.GetProperty("providerKey").GetString() == "identomat");
        FrontendFixtureContract.ShouldMatchShape(identomatChoice,
            FrontendFixtureContract.Load("tenant-kyc.json", "identomatChoice"));
    }

    private string Callback(IdentomatSession session, string claimedResult)
        => IdentomatVendor.CallbackBody(Flow.CallbackApiKey, session.Id, claimedResult);

    /// <summary>Onboards a tenant with Identomat assigned and enrols one custodial member.</summary>
    private async Task<(FlowTenant Tenant, string Subject, Guid MemberId)> EnrolMemberAsync()
    {
        var tenant = await OnboardTenantAsync();
        await AssignIdentomatToTenantAsync(tenant.TenantId);
        var subject = "member-" + Guid.NewGuid().ToString("N")[..12];
        var request = new HttpRequestMessage(HttpMethod.Put, $"api/tenant/custodial-accounts/{subject}");
        request.Headers.Add("Idempotency-Key", "enrol-" + subject);
        using var enrolled = await tenant.Client.SendAsync(request);
        var account = await ReadOkAsync<TenantCustodialAccountStatusResponse>(enrolled);
        account.IdentityReady.Should().BeTrue(account.UnavailableReason);
        return (tenant, subject, Guid.Parse(account.UserId!));
    }

    private async Task<TenantKycSessionResponse> BeginKycAsync(FlowTenant tenant, string subject, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/tenant/custodial-accounts/{subject}/kyc/session");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await tenant.Client.SendAsync(request);
        return await ReadOkAsync<TenantKycSessionResponse>(response);
    }

    private async Task<TenantCustodialAccountStatusResponse> AccountAsync(FlowTenant tenant, string subject)
    {
        using var response = await tenant.Client.GetAsync($"api/tenant/custodial-accounts/{subject}");
        return await ReadOkAsync<TenantCustodialAccountStatusResponse>(response);
    }

    private async Task<KycSubmission> SubmissionAsync(Guid memberId)
    {
        var member = SurrealLink.ToLink("user", memberId.ToString("N"));
        return (await RowsAsync(SurrealQuery<KycSubmission>.From().Where(row => row.UserId == member)))
            .OrderByDescending(row => row.SubmittedAt).First();
    }
}
