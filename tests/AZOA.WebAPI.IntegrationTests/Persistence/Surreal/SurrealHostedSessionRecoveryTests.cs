using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Models.Kyc;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealHostedSessionRecoveryTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private SurrealHostedSessionStore _store = null!;
    private ISurrealExecutor _executor = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "hosted_session", "hosted_session_provider_alias",
            "kyc_screening_monitor", "kyc_screening_result", "kyc_screening_poll_state", "kyc_submission",
            "tenant_kyc_provider_selection", "kyc_provider_profile", "kyc_authority_guard");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new SurrealHostedSessionStore(_executor);
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task QuarantinePreservesAdmissionAndOriginalCreatorCanAttachBothAliases()
    {
        var candidate = Candidate();
        var id = Guid.ParseExact(candidate.Id, "N");
        await _store.ClaimAdmissionAsync(candidate);
        var send = await _store.TryBeginCreationAsync(id, candidate.CreationToken!, DateTimeOffset.UtcNow);
        var parked = await _store.ReconcileAdmissionAsync(send.Result!, Guid.NewGuid(), "Provider lookup inconclusive", null, DateTimeOffset.UtcNow);
        parked.Result!.State.Should().Be(HostedSessionStates.Claimed);
        parked.Result.ReconciliationAuditJson.Should().Contain("QuarantineSent");
        var replacement = Candidate();
        replacement.AdmissionKey = candidate.AdmissionKey;
        (await _store.ClaimAdmissionAsync(replacement)).Result!.Id.Should().Be(candidate.Id);
        var result = await _store.TryAttachProviderSessionAsync(id, send.Result!.StateVersion,
            "parent-one", "child-one", "https://widget.identomat.com/", null, candidate.CreationToken!);
        result.Result!.CreationCompletedAt.Should().NotBeNull();
        foreach (var alias in new[] { "parent-one", "child-one" })
            (await _store.GetByProviderSessionAsync(candidate.FlowKind, candidate.ProviderKey, alias)).Result!.Id.Should().Be(candidate.Id);
    }

    [Fact]
    public async Task ConflictingAlias_RollsBackSessionAttachment()
    {
        var first = Candidate();
        var second = Candidate();
        foreach (var row in new[] { first, second })
        {
            await _store.ClaimAdmissionAsync(row);
            await _store.TryBeginCreationAsync(Guid.ParseExact(row.Id, "N"), row.CreationToken!, DateTimeOffset.UtcNow);
        }
        await _store.TryAttachProviderSessionAsync(Guid.ParseExact(first.Id, "N"), 1,
            "parent-one", "shared-child", "https://widget.identomat.com/", null, first.CreationToken!);
        var collision = await _store.TryAttachProviderSessionAsync(Guid.ParseExact(second.Id, "N"), 1,
            "shared-child", "different-child", "https://widget.identomat.com/", null, second.CreationToken!);
        collision.Result.Should().BeNull();
        var unchanged = await _store.GetByIdAsync(Guid.ParseExact(second.Id, "N"));
        unchanged.Result!.ProviderSessionId.Should().BeNull();
        unchanged.Result.State.Should().Be(HostedSessionStates.Claimed);
    }

    [Fact]
    public async Task ActiveNodeDefaultAttempt_AcquiresProviderSendOwnership()
    {
        var candidate = Candidate();
        candidate.SubjectUserId = Guid.NewGuid().ToString("N");
        candidate.AdmissionKey = "kyc-attempt:" + candidate.Id;
        var attempt = new KycSubmission
        {
            Id = candidate.Id, UserId = candidate.SubjectUserId, Provider = KycProvider.IDENTOMAT,
            ProviderKey = candidate.ProviderKey, ProviderSessionId = candidate.Id, HostedVerification = true,
            Status = KycStatus.PENDING, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            SubmittedAt = candidate.CreatedDate, CreatedDate = candidate.CreatedDate,
        };
        await new SurrealKycStore(_executor).UpsertSubmissionAsync(attempt);
        await _store.ClaimAdmissionAsync(candidate);
        var started = await _store.TryBeginCreationAsync(Guid.ParseExact(candidate.Id, "N"), candidate.CreationToken!, DateTimeOffset.UtcNow);
        started.IsError.Should().BeFalse();
        started.Result.Should().NotBeNull();
        (await _store.GetByIdAsync(Guid.ParseExact(candidate.Id, "N"))).Result!.CreationStartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ExpiredAttempt_PreventsProviderSendOwnership()
    {
        var candidate = Candidate();
        candidate.SubjectUserId = Guid.NewGuid().ToString("N");
        candidate.AdmissionKey = "kyc-attempt:" + candidate.Id;
        var attempt = new KycSubmission
        {
            Id = candidate.Id, UserId = candidate.SubjectUserId, Provider = KycProvider.IDENTOMAT,
            ProviderKey = candidate.ProviderKey, ProviderSessionId = candidate.Id, HostedVerification = true,
            Status = KycStatus.PENDING, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            SubmittedAt = candidate.CreatedDate, CreatedDate = candidate.CreatedDate,
        };
        await new SurrealKycStore(_executor).UpsertSubmissionAsync(attempt);
        await _store.ClaimAdmissionAsync(candidate);
        var started = await _store.TryBeginCreationAsync(Guid.ParseExact(candidate.Id, "N"), candidate.CreationToken!, DateTimeOffset.UtcNow);
        started.Result.Should().BeNull();
        (await _store.GetByIdAsync(Guid.ParseExact(candidate.Id, "N"))).Result!.CreationStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task ChangedOrDisabledTenantAuthority_PreventsSuspendedCreatorSend()
    {
        var tenant = Guid.NewGuid().ToString("N");
        var candidate = Candidate();
        candidate.SubjectUserId = Guid.NewGuid().ToString("N");
        candidate.TenantId = tenant;
        candidate.AdmissionKey = "kyc-attempt:" + candidate.Id;
        await new SurrealKycStore(_executor).UpsertSubmissionAsync(new KycSubmission
        {
            Id = candidate.Id, UserId = candidate.SubjectUserId, TenantId = tenant,
            Provider = KycProvider.IDENTOMAT, ProviderKey = candidate.ProviderKey,
            ProviderSelectionVersion = 1, ProviderTrustRevision = 1,
            ProviderSessionId = candidate.Id, HostedVerification = true, Status = KycStatus.PENDING,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30), SubmittedAt = candidate.CreatedDate, CreatedDate = candidate.CreatedDate,
        });
        await _executor.ExecuteAsync(SurrealWriter.Upsert(new TenantKycProviderSelection
        {
            Id = tenant, TenantId = "user:" + tenant, ProviderKey = candidate.ProviderKey, SelectionVersion = 2,
            UpdatedByUserId = "user:" + tenant, UpdatedAt = DateTimeOffset.UtcNow,
        }));
        var profile = new KycProviderProfile
        {
            Id = candidate.ProviderKey, AdapterKey = "identomat", DisplayName = "Test Identomat", Enabled = true,
            PolicyVersion = "v1", AssuranceLevel = "identity", TrustRevision = 1,
            UpdatedByUserId = "user:" + tenant, UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _executor.ExecuteAsync(SurrealWriter.Upsert(profile));
        await _store.ClaimAdmissionAsync(candidate);
        var id = Guid.ParseExact(candidate.Id, "N");
        (await _store.TryBeginCreationAsync(id, candidate.CreationToken!, DateTimeOffset.UtcNow)).Result.Should().BeNull();
        await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<TenantKycProviderSelection>(tenant).Where(seed => true).Set(row => row.SelectionVersion, 1L).Build());
        profile.Enabled = false;
        await _executor.ExecuteAsync(SurrealWriter.Upsert(profile));
        (await _store.TryBeginCreationAsync(id, candidate.CreationToken!, DateTimeOffset.UtcNow)).Result.Should().BeNull();
        (await _store.GetByIdAsync(id)).Result!.CreationStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task AmlPollAndMonitorMutationsPersistTheirActualFieldsAndEraseBothTables()
    {
        var screening = new SurrealKycScreeningStore(_executor);
        var now = DateTimeOffset.UtcNow;
        var monitor = new KycScreeningMonitor
        {
            Id = Guid.NewGuid().ToString("N"), ProviderMonitorId = "provider-monitor", TenantScope = "node",
            QueryFullName = "Fixture Subject", QueryBirthdayTimeFrom = "", LineageKey = "attempt-lineage",
            State = KycMonitorState.ACTIVE, RegisteredAt = now, CreatedDate = now, RetentionExpiresAt = now.AddMinutes(-1),
        };
        await screening.SaveMonitorAsync(monitor);
        await screening.RecordMonitorFollowUpAsync(monitor.Id, "2026-10-15T10:00:00Z", now);
        var persisted = await _executor.QuerySingleAsync<KycScreeningMonitor>(SurrealQuery<KycScreeningMonitor>.Key(monitor.Id));
        persisted!.LastObservedUpdateDate.Should().Be("2026-10-15T10:00:00Z");
        await screening.RecordPollFailureAsync(now);
        await screening.RecordPollFailureAsync(now.AddSeconds(1));
        (await screening.GetPollStateAsync()).Result!.ConsecutiveFailures.Should().Be(2);
        await screening.RecordPollSuccessAsync("2026-10-15T10:00:00Z", now.AddSeconds(2), 3);
        var poll = (await screening.GetPollStateAsync()).Result!;
        poll.ConsecutiveFailures.Should().Be(0);
        poll.BackoffUntil.Should().BeNull();
        poll.HighWaterMarkDate.Should().Be("2026-10-15T10:00:00Z");
        poll.LastPollChangedCount.Should().Be(3);
        var evidence = new KycScreeningResult
        {
            Id = Guid.NewGuid().ToString("N"), QueryFullName = "Fixture Subject", QueryBirthdayTimeFrom = "",
            LineageKey = monitor.LineageKey, Decision = KycScreeningDecision.HIT, PolicyVersion = "test",
            ScreenedAt = now, CreatedDate = now, RetentionExpiresAt = now.AddMinutes(-1),
        };
        await screening.SaveResultAsync(evidence);
        (await screening.EraseRetentionExpiredAsync(now, "Fixture retention expiry", 1)).Result.Should().Be(2);
        var erased = await _executor.QuerySingleAsync<KycScreeningResult>(SurrealQuery<KycScreeningResult>.Key(evidence.Id));
        erased!.QueryFullName.Should().BeNull();
        erased.Decision.Should().Be(KycScreeningDecision.HIT);
        erased.LineageKey.Should().Be(monitor.LineageKey);
    }

    [Fact]
    public async Task ReleasedUnsentAdmission_RejectsSuspendedCreatorToken()
    {
        var candidate = Candidate();
        await _store.ClaimAdmissionAsync(candidate);
        var released = await _store.ReconcileAdmissionAsync(candidate, Guid.NewGuid(), "No send began", null, DateTimeOffset.UtcNow);
        released.Result!.State.Should().Be(HostedSessionStates.Abandoned);
        (await _store.TryBeginCreationAsync(Guid.ParseExact(candidate.Id, "N"), candidate.CreationToken!, DateTimeOffset.UtcNow)).Result.Should().BeNull();
        var oldToken = candidate.CreationToken;
        candidate.CreationToken = Guid.NewGuid().ToString("N");
        var reclaimed = await _store.ClaimAdmissionAsync(candidate);
        reclaimed.Result!.ReconciliationAuditJson.Should().Contain("ReleaseUnsent");
        reclaimed.Result.CreationToken.Should().Be(candidate.CreationToken);
        reclaimed.Result.CreationClaimedAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-1));
        (await _store.ListReconciliationAsync(DateTimeOffset.UtcNow.AddMinutes(-5), 50)).Should().BeEmpty();
        (await _store.TryBeginCreationAsync(Guid.ParseExact(candidate.Id, "N"), oldToken!, DateTimeOffset.UtcNow)).Result.Should().BeNull();
        (await _store.TryBeginCreationAsync(Guid.ParseExact(candidate.Id, "N"), candidate.CreationToken!, DateTimeOffset.UtcNow)).Result.Should().NotBeNull();
    }

    private static HostedSession Candidate() => new()
    {
        Id = Guid.NewGuid().ToString("N"), AdmissionKey = Guid.NewGuid().ToString("N"),
        FlowKind = HostedSessionFlowKinds.Kyc, ProviderKey = "identomat",
        State = HostedSessionStates.Claimed, CreationToken = Guid.NewGuid().ToString("N"),
        CreatedDate = DateTimeOffset.UtcNow.AddMinutes(-10), ModifiedDate = DateTimeOffset.UtcNow,
    };
}
