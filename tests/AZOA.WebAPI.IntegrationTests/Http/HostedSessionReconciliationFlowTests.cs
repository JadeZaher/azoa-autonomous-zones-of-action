using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyb;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>
/// Operator recovery of an ambiguous hosted-session creation: the vendor created the session
/// but its response never arrived. See <c>Http/AGENTS.md</c> §flows.
/// </summary>
public sealed class HostedSessionReconciliationFlowTests(HttpFlowWebApplicationFactory factory) : HttpFlowTestBase(factory)
{
    private const string EvidenceReason = "Matched the vendor dashboard session to this tenant's admission key.";

    [Fact]
    public async Task An_operator_attaches_the_verified_vendor_session_and_the_tenant_resumes_it()
    {
        using var tenant = await OnboardTenantAsync();
        Flow.Identomat.LoseNextBeginResponse();

        using var ambiguous = await BeginRequestAsync(tenant, "lost-response");
        await ShouldReturnAsync(ambiguous, HttpStatusCode.Conflict);
        var vendorSession = Flow.Identomat.LatestBusinessSession!;
        var admission = (await RowsAsync(SurrealQuery<HostedSession>.From()
            .Where(row => row.FlowKind == HostedSessionFlowKinds.Kyb))).Should().ContainSingle().Which;
        var reconcileRoute = $"api/operator/hosted-sessions/{Guid.ParseExact(admission.Id, "N")}/reconcile";
        var recovery = new HostedSessionReconciliationRequest
        {
            OperatorReviewedSubjectAndProvider = true,
            ConfirmedProviderAbsence = false,
            Reason = EvidenceReason,
            ProviderSessionId = vendorSession.Id,
            RedirectUrl = $"https://{IdentomatVendor.WidgetHost}/?session_token={vendorSession.SessionToken}",
        };

        // A creation that may still be in flight is never reconciled out from under itself.
        using var tooEarly = await Operator.PostAsJsonAsync(reconcileRoute, recovery, JsonOptions);
        await ShouldReturnAsync(tooEarly, HttpStatusCode.Conflict);

        await WriteAsync(SurrealWriter.UpdateOnly<HostedSession>(admission.Id).Where(seed => true)
            .Set(row => row.CreationClaimedAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-6)).Build());
        (await StatusAsync(tenant)).Status.Should().Be("NEEDS_OPERATOR");

        using var queue = await Operator.GetAsync("api/operator/hosted-sessions/reconciliation");
        var listed = await ReadOkAsync<JsonElement>(queue);
        listed.EnumerateArray().Select(row => row.GetProperty("sessionId").GetString())
            .Should().Contain(admission.Id, "the stranded admission is on the operator's queue");

        using var reconciled = await Operator.PostAsJsonAsync(reconcileRoute, recovery, JsonOptions);
        (await ReadOkAsync<JsonElement>(reconciled)).GetProperty("status").GetString().Should().Be("Recovered");

        (await StatusAsync(tenant)).Status.Should().Be("PENDING");
        var resumed = await ReadOkAsync<KybSessionResponse>(await BeginRequestAsync(tenant, "after-recovery"));
        resumed.Resumed.Should().BeTrue();
        resumed.VerificationUrl.Should().Be(recovery.RedirectUrl);

        vendorSession.Verdict = IdentomatVerdict.Approved;
        await PostCallbackAsync("api/kyc/callback/identomat",
            IdentomatVendor.CallbackBody(Flow.CallbackApiKey, vendorSession.Id, "approved"));
        (await StatusAsync(tenant)).Status.Should().Be("APPROVED");
    }

    private static Task<HttpResponseMessage> BeginRequestAsync(FlowTenant tenant, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/tenant/business-verification/session");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return tenant.Client.SendAsync(request);
    }

    private async Task<KybStatusResponse> StatusAsync(FlowTenant tenant)
    {
        using var response = await tenant.Client.GetAsync("api/tenant/business-verification");
        return await ReadOkAsync<KybStatusResponse>(response);
    }
}
