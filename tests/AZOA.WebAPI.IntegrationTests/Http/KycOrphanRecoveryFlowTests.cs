using System.Net;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.HostedSessions;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>
/// IDENTOMAT-RECOVERY over HTTP (round-4 L2): a lost <c>/begin</c> response leaves an orphan the vendor
/// cannot look up; after the bounded wait the person's next begin supersedes it, the orphan's vendor
/// session stays inert, and unknown-session callbacks decide nothing. See <c>Http/AGENTS.md</c> §flows.
/// </summary>
public sealed class KycOrphanRecoveryFlowTests(HttpFlowWebApplicationFactory factory) : HttpFlowTestBase(factory)
{
    private const string CallbackIngress = "api/kyc/callback/identomat";

    [Fact]
    public async Task A_lost_begin_response_is_superseded_after_the_bounded_wait_and_its_vendor_session_stays_inert()
    {
        var user = await SeedUserAsync();
        using var client = Factory.CreateAuthenticatedClientForUser(user.Id);
        Flow.Identomat.LoseNextBeginResponse();

        using var uncertain = await client.PostAsync("api/kyc/session", null);
        await ShouldReturnAsync(uncertain, HttpStatusCode.Conflict);
        var orphanVendorSession = Flow.Identomat.LatestIdentitySessionFor(user.Id)!;
        using var tooSoon = await client.PostAsync("api/kyc/session", null);
        await ShouldReturnAsync(tooSoon, HttpStatusCode.Conflict);
        Flow.Identomat.Sessions.Count(session => session.ClientUserId == user.Id.ToString("N")).Should().Be(1,
            "inside the bounded wait the possibly-sent create is never repeated");

        var orphan = (await AdmissionsAsync(user.Id)).Should().ContainSingle().Which;
        await WriteAsync(SurrealWriter.UpdateOnly<HostedSession>(orphan.Id).Where(seed => true)
            .Set(row => row.CreationClaimedAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-6)).Build());

        using var fresh = await client.PostAsync("api/kyc/session", null);
        var started = await ReadOkAsync<KycSessionStartModel>(fresh);
        started.VerificationUrl.Should().StartWith($"https://{IdentomatVendor.WidgetHost}/");
        var superseded = (await AdmissionsAsync(user.Id)).Single(row => row.Id == orphan.Id);
        superseded.State.Should().Be(HostedSessionStates.Abandoned);
        superseded.OutcomeDetail.Should().Be(HostedSessionRecoveryOutcomes.SupersededOrphan);

        // The orphan's vendor session exists and is approved, but nothing in AZOA is bound to it.
        orphanVendorSession.Verdict = IdentomatVerdict.Approved;
        using var orphanCallback = await PostCallbackAsync(CallbackIngress,
            IdentomatVendor.CallbackBody(Flow.CallbackApiKey, orphanVendorSession.Id, "APPROVED"));
        orphanCallback.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await StatusAsync(client)).Status.Should().Be(KycStatus.PENDING);
    }

    [Fact]
    public async Task A_second_orphan_within_a_day_waits_for_an_operator()
    {
        var user = await SeedUserAsync();
        using var client = Factory.CreateAuthenticatedClientForUser(user.Id);
        await OrphanTheNextBeginAsync(client, user.Id);
        var replacement = await ReadOkAsync<KycSessionStartModel>(await client.PostAsync("api/kyc/session", null));

        // The replacement attempt lapses, and the next attempt's create response is lost too.
        await WriteAsync(SurrealWriter.UpdateOnly<KycSubmission>(replacement.ProviderSessionId!).Where(seed => true)
            .Set(row => row.ExpiresAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-1)).Build());
        await OrphanTheNextBeginAsync(client, user.Id);

        using var refused = await client.PostAsync("api/kyc/session", null);
        await ShouldReturnAsync(refused, HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("NeedsOperator");
        (await AdmissionsAsync(user.Id)).Count(row => row.OutcomeDetail == HostedSessionRecoveryOutcomes.SupersededOrphan)
            .Should().Be(1);
    }

    [Fact]
    public async Task A_callback_for_a_session_the_node_never_attached_changes_nothing()
    {
        var user = await SeedUserAsync();
        using var client = Factory.CreateAuthenticatedClientForUser(user.Id);
        await ReadOkAsync<KycSessionStartModel>(await client.PostAsync("api/kyc/session", null));
        var before = await AdmissionsAsync(user.Id);

        using var unknown = await PostCallbackAsync(CallbackIngress,
            IdentomatVendor.CallbackBody(Flow.CallbackApiKey, "idm-session-" + Guid.NewGuid().ToString("N"), "APPROVED"));

        unknown.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var after = await AdmissionsAsync(user.Id);
        after.Select(row => (row.Id, row.State, row.StateVersion, row.CallbackCount))
            .Should().BeEquivalentTo(before.Select(row => (row.Id, row.State, row.StateVersion, row.CallbackCount)));
        (await StatusAsync(client)).Status.Should().Be(KycStatus.PENDING);
    }

    /// <summary>Begins with the vendor's response lost (409), then ages the orphan past the bounded wait.</summary>
    private async Task OrphanTheNextBeginAsync(HttpClient client, Guid user)
    {
        Flow.Identomat.LoseNextBeginResponse();
        using var uncertain = await client.PostAsync("api/kyc/session", null);
        await ShouldReturnAsync(uncertain, HttpStatusCode.Conflict);
        var open = (await AdmissionsAsync(user)).Single(row => row.State == HostedSessionStates.Claimed);
        await WriteAsync(SurrealWriter.UpdateOnly<HostedSession>(open.Id).Where(seed => true)
            .Set(row => row.CreationClaimedAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-6)).Build());
    }

    private async Task<IReadOnlyList<HostedSession>> AdmissionsAsync(Guid user)
    {
        var link = SurrealLink.ToLink("user", user.ToString("N"));
        return await RowsAsync(SurrealQuery<HostedSession>.From()
            .Where(row => row.SubjectUserId == link && row.FlowKind == HostedSessionFlowKinds.Kyc));
    }

    private async Task<KycSubmissionModel> StatusAsync(HttpClient client)
    {
        using var response = await client.GetAsync("api/kyc/status");
        return await ReadOkAsync<KycSubmissionModel>(response);
    }
}
