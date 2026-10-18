using System.Net;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.HostedSessions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>
/// First-party identity verification (F-C3/F-H1, round-4 L2): a person verifies themself through
/// <c>POST /api/kyc/session</c>, and the node's value gate opens only from Identomat's authoritative
/// result, whether the callback arrives, is lost (user refresh / outcome poll) or never comes.
/// See <c>Http/AGENTS.md</c> §flows.
/// </summary>
public sealed class KycFirstPartyFlowTests(HttpFlowWebApplicationFactory factory) : HttpFlowTestBase(factory)
{
    private const string CallbackIngress = "api/kyc/callback/identomat";

    [Fact]
    public async Task Begin_callback_result_opens_the_value_gate_and_a_replayed_callback_changes_nothing()
    {
        var (user, client) = await PersonAsync();
        var started = await BeginAsync(client);
        started.HostedVerification.Should().BeTrue();
        started.VerificationUrl.Should().StartWith($"https://{IdentomatVendor.WidgetHost}/");
        (await GateAsync(user)).Should().BeFalse("nothing is verified yet");

        var vendorSession = Flow.Identomat.LatestIdentitySessionFor(user)!;
        vendorSession.Verdict = IdentomatVerdict.Approved;
        using var callback = await PostCallbackAsync(CallbackIngress,
            IdentomatVendor.CallbackBody(Flow.CallbackApiKey, vendorSession.Id, "REJECTED"));
        callback.StatusCode.Should().Be(HttpStatusCode.Accepted);

        (await StatusAsync(client)).Status.Should().Be(KycStatus.APPROVED, "the re-fetched result decides, not the callback body");
        (await GateAsync(user)).Should().BeTrue();

        var decided = await SubmissionAsync(user);
        using var replay = await PostCallbackAsync(CallbackIngress,
            IdentomatVendor.CallbackBody(Flow.CallbackApiKey, vendorSession.Id, "APPROVED"));
        var afterReplay = await SubmissionAsync(user);
        afterReplay.ModifiedDate.Should().Be(decided.ModifiedDate);
        afterReplay.ExpiresAt.Should().Be(decided.ExpiresAt);
    }

    [Fact]
    public async Task A_vendor_rejection_keeps_the_gate_closed()
    {
        var (user, client) = await PersonAsync();
        await BeginAsync(client);
        var vendorSession = Flow.Identomat.LatestIdentitySessionFor(user)!;
        vendorSession.Verdict = IdentomatVerdict.Rejected;

        await PostCallbackAsync(CallbackIngress, IdentomatVendor.CallbackBody(Flow.CallbackApiKey, vendorSession.Id, "APPROVED"));

        (await StatusAsync(client)).Status.Should().Be(KycStatus.REJECTED);
        (await GateAsync(user)).Should().BeFalse();
    }

    [Fact]
    public async Task An_abandoned_attempt_never_opens_the_gate_even_when_the_vendor_approves_late()
    {
        var (user, client) = await PersonAsync();
        await BeginAsync(client);
        var attempt = await SubmissionAsync(user);
        await WriteAsync(SurrealWriter.UpdateOnly<KycSubmission>(attempt.Id).Where(seed => true)
            .Set(row => row.ExpiresAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-1)).Build());
        var vendorSession = Flow.Identomat.LatestIdentitySessionFor(user)!;
        vendorSession.Verdict = IdentomatVerdict.Approved;

        await PostCallbackAsync(CallbackIngress, IdentomatVendor.CallbackBody(Flow.CallbackApiKey, vendorSession.Id, "APPROVED"));

        (await StatusAsync(client)).Status.Should().Be(KycStatus.EXPIRED);
        (await GateAsync(user)).Should().BeFalse("an expired attempt is never approved by a late verdict");
        (await HostedSessionAsync(attempt.Id)).State.Should().BeOneOf(HostedSessionStates.Completed, HostedSessionStates.Failed);
    }

    [Fact]
    public async Task A_lost_callback_is_recovered_by_the_users_refresh()
    {
        var (user, client) = await PersonAsync();
        await BeginAsync(client);
        Flow.Identomat.LatestIdentitySessionFor(user)!.Verdict = IdentomatVerdict.Approved;

        using var refreshed = await client.PostAsync("api/kyc/refresh", null);
        (await ReadOkAsync<KycSubmissionModel>(refreshed)).Status.Should().Be(KycStatus.APPROVED);
        (await GateAsync(user)).Should().BeTrue();
    }

    [Fact]
    public async Task A_lost_callback_is_recovered_by_the_outcome_poll_and_manual_review_extends_the_attempt()
    {
        var (user, client) = await PersonAsync();
        await BeginAsync(client);
        var attempt = await SubmissionAsync(user);

        // The fake answers MANUAL_CHECK while undecided: the vendor's human review must not let the attempt lapse.
        await SweepAsync();
        var inReview = await EventuallySubmissionAsync(user, row => row.Status == KycStatus.IN_REVIEW);
        inReview.Status.Should().Be(KycStatus.IN_REVIEW);
        inReview.ExpiresAt.Should().BeAfter(attempt.ExpiresAt!.Value.AddDays(1));
        (await GateAsync(user)).Should().BeFalse();

        Flow.Identomat.LatestIdentitySessionFor(user)!.Verdict = IdentomatVerdict.Approved;
        await WriteAsync(SurrealWriter.UpdateOnly<HostedSession>(attempt.Id).Where(seed => true)
            .Unset(row => row.NextPollAt).Build());
        await SweepAsync();
        (await EventuallyStatusAsync(client, KycStatus.APPROVED)).Should().Be(KycStatus.APPROVED);
        (await GateAsync(user)).Should().BeTrue();
    }

    [Fact]
    public async Task An_operator_refreshes_a_stalled_identomat_session()
    {
        var (user, client) = await PersonAsync();
        await BeginAsync(client);
        var attempt = await SubmissionAsync(user);
        Flow.Identomat.LatestIdentitySessionFor(user)!.Verdict = IdentomatVerdict.Rejected;

        using var refreshed = await Operator.PostAsync($"api/operator/hosted-sessions/{Guid.ParseExact(attempt.Id, "N")}/refresh", null);
        await ShouldReturnAsync(refreshed, HttpStatusCode.OK);

        (await StatusAsync(client)).Status.Should().Be(KycStatus.REJECTED);
        (await HostedSessionAsync(attempt.Id)).ReconciliationAuditJson.Should().Contain("OperatorRefresh");
    }

    [Fact]
    public async Task A_delegated_tenant_caller_cannot_start_or_refresh_a_first_party_verification()
    {
        var (_, _) = await PersonAsync();
        using var tenant = await OnboardTenantAsync();

        using var begin = await tenant.Client.PostAsync("api/kyc/session", null);
        begin.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        using var refresh = await tenant.Client.PostAsync("api/kyc/refresh", null);
        refresh.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    private async Task<(Guid User, HttpClient Client)> PersonAsync()
    {
        var user = await SeedUserAsync();
        return (user.Id, Factory.CreateAuthenticatedClientForUser(user.Id));
    }

    private async Task<KycSessionStartModel> BeginAsync(HttpClient client)
    {
        using var response = await client.PostAsync("api/kyc/session", null);
        return await ReadOkAsync<KycSessionStartModel>(response);
    }

    private async Task<KycSubmissionModel> StatusAsync(HttpClient client)
    {
        using var response = await client.GetAsync("api/kyc/status");
        return await ReadOkAsync<KycSubmissionModel>(response);
    }

    private async Task<KycStatus> EventuallyStatusAsync(HttpClient client, KycStatus expected)
    {
        var status = (await StatusAsync(client)).Status;
        for (var attempt = 0; attempt < 40 && status != expected; attempt++)
        {
            await Task.Delay(250);
            status = (await StatusAsync(client)).Status;
        }
        return status;
    }

    /// <summary>The host's real value gate (RiskHoldValueGate over the merged KYC gate).</summary>
    private async Task<bool> GateAsync(Guid user)
    {
        using var scope = Factory.Services.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<IRealValueKycGate>();
        var decision = await gate.RequireCurrentApprovalAsync(user);
        return !decision.IsError && decision.Result;
    }

    /// <summary>Runs one outcome-poll sweep exactly as the hosted worker does.</summary>
    private async Task SweepAsync()
    {
        var worker = Factory.Services.GetServices<IHostedService>().OfType<HostedOutcomePollWorker>().Single();
        using var scope = Factory.Services.CreateScope();
        await worker.SweepAsync(scope.ServiceProvider, CancellationToken.None);
    }

    /// <summary>The hosted worker may win the poll claim; either way the committed row converges.</summary>
    private async Task<KycSubmission> EventuallySubmissionAsync(Guid user, Func<KycSubmission, bool> settled)
    {
        var row = await SubmissionAsync(user);
        for (var attempt = 0; attempt < 40 && !settled(row); attempt++)
        {
            await Task.Delay(250);
            row = await SubmissionAsync(user);
        }
        return row;
    }

    private async Task<KycSubmission> SubmissionAsync(Guid user)
    {
        var link = SurrealLink.ToLink("user", user.ToString("N"));
        return (await RowsAsync(SurrealQuery<KycSubmission>.From().Where(row => row.UserId == link)))
            .OrderByDescending(row => row.SubmittedAt).First();
    }

    private async Task<HostedSession> HostedSessionAsync(string id)
        => (await RowsAsync(SurrealQuery<HostedSession>.Key(id))).Single();
}
