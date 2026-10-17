using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.HostedSessions;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// IDENTOMAT-RECOVERY and outcome-poll persistence on a real SurrealDB 3.x namespace (round-4 L2):
/// late create responses after supersession are deleted, poll claims have one winner across
/// replicas, and the NONE-valued poll columns select exactly the due rows.
/// Driven through <see cref="HostedSessionFlow"/> and the store, the hosted-session public seams;
/// the HTTP flow cannot hold a vendor response open across a supersede.
/// </summary>
public sealed class SurrealHostedSessionOrphanRecoveryTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealHostedSessionStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "hosted_session", "hosted_session_provider_alias", "user");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new(_executor);
    }

    public async Task DisposeAsync() { _connection.Dispose(); await SurrealTestSchema.DropAsync(_namespace); }

    [Fact]
    public async Task A_create_response_that_lands_after_supersession_is_deleted_and_never_attaches()
    {
        var provider = new HeldCreateProvider("vendor-late-" + Guid.NewGuid().ToString("N"));
        var flow = new HostedSessionFlow(_store, new NoResume());
        var request = Request(Guid.NewGuid());
        var begin = flow.BeginAsync(provider, request);
        await provider.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var inFlight = (await _store.GetByIdAsync(request.SessionId!.Value)).Result!;
        inFlight.CreationStartedAt.Should().NotBeNull("the provider request was fenced before sending");
        (await _store.TrySupersedeOrphanAsync(inFlight, DateTimeOffset.UtcNow)).Result.Should().NotBeNull();

        provider.ReleaseCreate();
        var started = await begin.WaitAsync(TimeSpan.FromSeconds(30));

        started.IsError.Should().BeTrue();
        provider.Deleted.Should().Equal(provider.ProviderSessionId);
        var closed = (await _store.GetByIdAsync(request.SessionId.Value)).Result!;
        closed.State.Should().Be(HostedSessionStates.Abandoned);
        closed.OutcomeDetail.Should().Be(HostedSessionRecoveryOutcomes.SupersededOrphan);
        closed.ProviderSessionId.Should().BeNull();
        closed.ReconciliationAuditJson.Should().Contain("LateProviderSessionDeleted");
        (await _store.GetByProviderSessionAsync(HostedSessionFlowKinds.Kyc, provider.ProviderKey, provider.ProviderSessionId))
            .Result.Should().BeNull("a callback naming the late session finds nothing to decide");
    }

    [Fact]
    public async Task Concurrent_poll_claims_on_one_due_session_have_exactly_one_winner()
    {
        var session = await AttachedSessionAsync();
        var now = DateTimeOffset.UtcNow;
        var due = await _store.ListPollDueAsync(now, 100);
        var observed = due.Should().ContainSingle(row => row.Id == session.Id).Which;
        observed.NextPollAt.Should().BeNull("a never-polled session is due immediately");

        var claims = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => _store.TryClaimPollAsync(observed, now.AddMinutes(1))));

        claims.Count(won => won).Should().Be(1);
        (await _store.ListPollDueAsync(now, 100)).Should().NotContain(row => row.Id == session.Id);
        var reclaimed = (await _store.ListPollDueAsync(now.AddMinutes(2), 100)).Should().ContainSingle(row => row.Id == session.Id).Which;
        reclaimed.PollAttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task The_supersede_budget_counts_only_this_subjects_orphans_in_the_window()
    {
        var subject = Guid.NewGuid();
        var orphan = await SentUnattachedAsync(subject);
        var other = await SentUnattachedAsync(Guid.NewGuid());
        await _store.TrySupersedeOrphanAsync(orphan, DateTimeOffset.UtcNow);
        await _store.TrySupersedeOrphanAsync(other, DateTimeOffset.UtcNow);

        (await _store.CountSupersededSinceAsync(HostedSessionFlowKinds.Kyc, subject, DateTimeOffset.UtcNow.AddDays(-1))).Should().Be(1);
        (await _store.CountSupersededSinceAsync(HostedSessionFlowKinds.Kyb, subject, DateTimeOffset.UtcNow.AddDays(-1))).Should().Be(0);
        (await _store.CountSupersededSinceAsync(HostedSessionFlowKinds.Kyc, subject, DateTimeOffset.UtcNow.AddMinutes(1))).Should().Be(0);
        (await _store.TrySupersedeOrphanAsync(orphan, DateTimeOffset.UtcNow)).Result.Should().BeNull("a superseded row cannot be superseded twice");
    }

    private async Task<HostedSession> SentUnattachedAsync(Guid subject)
    {
        var request = Request(subject);
        var candidate = Candidate(request);
        var claimed = (await _store.ClaimAdmissionAsync(candidate)).Result!;
        return (await _store.TryBeginCreationAsync(Guid.ParseExact(claimed.Id, "N"), candidate.CreationToken!, DateTimeOffset.UtcNow)).Result!;
    }

    private async Task<HostedSession> AttachedSessionAsync()
    {
        var sent = await SentUnattachedAsync(Guid.NewGuid());
        return (await _store.TryAttachProviderSessionAsync(Guid.ParseExact(sent.Id, "N"), sent.StateVersion,
            "vendor-" + Guid.NewGuid().ToString("N"), null, "https://widget.vendor.test/?session_token=x", null,
            sent.CreationToken!)).Result!;
    }

    private static HostedSessionRequest Request(Guid subject) => new()
    {
        FlowKind = HostedSessionFlowKinds.Kyc, SubjectUserId = subject,
        AdmissionKey = "recovery-test:" + Guid.NewGuid().ToString("N"), SessionId = Guid.NewGuid(),
    };

    private static HostedSession Candidate(HostedSessionRequest request) => new()
    {
        Id = request.SessionId!.Value.ToString("N"), FlowKind = request.FlowKind, ProviderKey = "identomat",
        AdmissionKey = request.AdmissionKey, CreationToken = Guid.NewGuid().ToString("N"),
        CreationClaimedAt = DateTimeOffset.UtcNow, SubjectUserId = request.SubjectUserId.ToString("N"),
        State = HostedSessionStates.Claimed, CreatedDate = DateTimeOffset.UtcNow, ModifiedDate = DateTimeOffset.UtcNow,
    };

    /// <summary>Holds the vendor create response open until the test releases it, like a slow network.</summary>
    private sealed class HeldCreateProvider(string providerSessionId) : IHostedSessionProvider, IDeletableHostedSessionProvider
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderSessionId { get; } = providerSessionId;
        public TaskCompletionSource CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Deleted { get; } = [];
        public string ProviderKey => "identomat";
        public string FlowKind => HostedSessionFlowKinds.Kyc;

        public void ReleaseCreate() => _release.TrySetResult();

        public HostedSessionCapabilities GetHostedCapabilities()
            => new() { ProviderKey = ProviderKey, FlowKind = FlowKind, Available = true };

        public async Task<AZOAResult<HostedSessionCreation>> CreateHostedSessionAsync(HostedSessionRequest request, CancellationToken ct = default)
        {
            CreateStarted.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return AZOAResult<HostedSessionCreation>.Success(new HostedSessionCreation
            {
                ProviderSessionId = ProviderSessionId, RedirectUrl = $"https://widget.vendor.test/?session_token={ProviderSessionId}",
            });
        }

        public Task<AZOAResult<bool>> DeleteProviderSessionAsync(string providerSessionId, CancellationToken ct = default)
        {
            lock (Deleted) Deleted.Add(providerSessionId);
            return Task.FromResult(AZOAResult<bool>.Success(true));
        }

        public AZOAResult<HostedCallbackTrigger> VerifyCallback(string rawBody)
            => AZOAResult<HostedCallbackTrigger>.Failure("not used");

        public Task<AZOAResult<HostedSessionOutcome>> FetchAuthoritativeOutcomeAsync(string providerSessionId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<HostedSessionOutcome>.Failure("not used"));
    }

    private sealed class NoResume : IHostedSessionOutcomeHandler
    {
        public Task<AZOAResult<string?>> ResumeAsync(HostedSession session, HostedSessionOutcome outcome, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<string?>.Failure("not used"));
    }
}
