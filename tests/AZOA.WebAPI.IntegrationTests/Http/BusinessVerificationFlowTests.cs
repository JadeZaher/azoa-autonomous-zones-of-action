using System.Net;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyb;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>Tenant business verification (KYB) end to end through the public routes. See <c>Http/AGENTS.md</c> §flows.</summary>
public sealed class BusinessVerificationFlowTests(HttpFlowWebApplicationFactory factory) : HttpFlowTestBase(factory)
{
    /// <summary>The company-wide callback URL Identomat is configured with (shared KYC/KYB ingress).</summary>
    private const string SharedIngress = "api/kyc/callback/identomat";

    /// <summary>The KYB-only ingress for installations with a dedicated vendor company.</summary>
    private const string DedicatedIngress = "api/kyb/identomat/callback";

    [Theory]
    [InlineData(SharedIngress)]
    [InlineData(DedicatedIngress)]
    public async Task Approval_comes_from_the_vendor_result_and_a_replayed_callback_changes_nothing(string ingress)
    {
        using var tenant = await OnboardTenantAsync();
        var capabilities = await ReadOkAsync<HostedSessionCapabilities>(
            await tenant.Client.GetAsync("api/tenant/business-verification/capabilities"));
        capabilities.Available.Should().BeTrue(string.Join(", ", capabilities.MissingConfigurationKeys));

        var started = await BeginAsync(tenant, "first-browser-request");
        started.Verification.Status.Should().Be("PENDING");
        started.VerificationUrl.Should().StartWith($"https://{IdentomatVendor.WidgetHost}/");
        var session = Flow.Identomat.LatestBusinessSession!;

        // Still in review at the vendor: a callback CLAIMING approval decides nothing.
        (await PostCallbackAsync(ingress, Callback(session, "approved"))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await StatusAsync(tenant)).Status.Should().Be("PENDING");

        // The vendor approves; the callback's own claim is ignored either way.
        session.Verdict = IdentomatVerdict.Approved;
        (await PostCallbackAsync(ingress, Callback(session, "rejected"))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var approved = await StatusAsync(tenant);
        approved.Status.Should().Be("APPROVED");
        approved.Verified.Should().BeTrue();
        approved.BeneficiaryCount.Should().Be(1);
        approved.ReviewedAt.Should().NotBeNull();

        // Vendors redeliver. The redelivery converges on the committed decision without rewriting it.
        var committed = await VerificationRowAsync(tenant.TenantId);
        (await PostCallbackAsync(ingress, Callback(session, "approved"))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var afterReplay = await VerificationRowAsync(tenant.TenantId);
        afterReplay.Version.Should().Be(committed.Version);
        afterReplay.ReviewedAt.Should().Be(committed.ReviewedAt);
        (await StatusAsync(tenant)).Should().BeEquivalentTo(approved);
    }

    [Fact]
    public async Task A_callback_without_the_shared_secret_is_acknowledged_identically_and_decides_nothing()
    {
        using var tenant = await OnboardTenantAsync();
        await BeginAsync(tenant, "request");
        var session = Flow.Identomat.LatestBusinessSession!;
        session.Verdict = IdentomatVerdict.Approved;

        using var forged = await PostCallbackAsync(SharedIngress,
            IdentomatVendor.CallbackBody("forged-" + Guid.NewGuid().ToString("N"), session.Id, "approved"));
        (await StatusAsync(tenant)).Status.Should().Be("PENDING");

        using var genuine = await PostCallbackAsync(SharedIngress, Callback(session, "approved"));
        (await StatusAsync(tenant)).Status.Should().Be("APPROVED", "the session was approvable, so only the key stopped the forged callback");

        // No oracle: an anonymous caller cannot tell a wrong key from a right one.
        forged.StatusCode.Should().Be(genuine.StatusCode);
        (await forged.Content.ReadAsStringAsync()).Should().Be(await genuine.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_rejected_review_is_terminal_until_the_tenant_starts_a_new_review()
    {
        using var tenant = await OnboardTenantAsync();
        var first = await BeginAsync(tenant, "original-request");
        var session = Flow.Identomat.LatestBusinessSession!;
        session.Verdict = IdentomatVerdict.Rejected;
        await PostCallbackAsync(SharedIngress, Callback(session, "approved"));

        var rejected = await StatusAsync(tenant);
        rejected.Status.Should().Be("REJECTED");
        rejected.Verified.Should().BeFalse();

        using var sameKey = await BeginRequestAsync(tenant, "original-request");
        sameKey.StatusCode.Should().Be(HttpStatusCode.Conflict, "replaying the original request must not reopen a decided review");

        var second = await BeginAsync(tenant, "new-review-request");
        second.Verification.Status.Should().Be("PENDING");
        second.Verification.AttemptId.Should().NotBe(first.Verification.AttemptId);
    }

    [Fact]
    public async Task Status_and_session_payloads_keep_the_shape_the_frontend_fixtures_declare()
    {
        using var tenant = await OnboardTenantAsync();
        using var capabilities = await tenant.Client.GetAsync("api/tenant/business-verification/capabilities");
        using var begun = await BeginRequestAsync(tenant, "contract-request");
        var session = Flow.Identomat.LatestBusinessSession!;
        session.Verdict = IdentomatVerdict.Approved;
        await PostCallbackAsync(SharedIngress, Callback(session, "approved"));
        using var status = await tenant.Client.GetAsync("api/tenant/business-verification");

        FrontendFixtureContract.ShouldMatchShape(await ResultElementAsync(capabilities),
            FrontendFixtureContract.Load("business-verification.json", "capabilitiesAvailable"));
        FrontendFixtureContract.ShouldMatchShape(await ResultElementAsync(begun),
            FrontendFixtureContract.Load("business-verification.json", "sessionStarted"));
        FrontendFixtureContract.ShouldMatchShape(await ResultElementAsync(status),
            FrontendFixtureContract.Load("business-verification.json", "approved"));
    }

    private string Callback(IdentomatSession session, string claimedResult)
        => IdentomatVendor.CallbackBody(Flow.CallbackApiKey, session.Id, claimedResult);

    private static Task<HttpResponseMessage> BeginRequestAsync(FlowTenant tenant, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/tenant/business-verification/session");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return tenant.Client.SendAsync(request);
    }

    private async Task<KybSessionResponse> BeginAsync(FlowTenant tenant, string idempotencyKey)
    {
        using var response = await BeginRequestAsync(tenant, idempotencyKey);
        return await ReadOkAsync<KybSessionResponse>(response);
    }

    private async Task<KybStatusResponse> StatusAsync(FlowTenant tenant)
    {
        using var response = await tenant.Client.GetAsync("api/tenant/business-verification");
        return await ReadOkAsync<KybStatusResponse>(response);
    }

    private async Task<KybVerification> VerificationRowAsync(Guid tenantId)
        => (await RowsAsync(SurrealQuery<KybVerification>.Key(tenantId.ToString("N")))).Single();
}
