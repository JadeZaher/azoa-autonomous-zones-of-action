using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealHostedProviderAttemptTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealHostedProviderAttemptStore _attempts = null!;
    private SurrealHostedSessionStore _sessions = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "hosted_session", "hosted_session_provider_alias", "hosted_provider_attempt",
            "hosted_provider_callback", "kyc_submission", "tenant_kyc_provider_selection", "kyc_provider_profile", "kyc_authority_guard",
            "kyc_screening_result", "kyc_screening_monitor", "kyc_identity");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
        _attempts = new(_executor); _sessions = new(_executor);
    }

    public async Task DisposeAsync() { _connection.Dispose(); await SurrealTestSchema.DropAsync(_namespace); }

    [Fact]
    public async Task BoundAmlAppendsShareOneEvidenceRevision_SubjectlessScreeningsNeverCreateEmptySubjectGuard()
    {
        var screening = new SurrealKycScreeningStore(_executor);
        var user = Guid.NewGuid().ToString("N"); var tenant = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow;
        var appends = Enumerable.Range(0, 8).Select(index => screening.SaveResultAsync(new KycScreeningResult
        {
            Id = Guid.NewGuid().ToString("N"), SubjectUserId = user, TenantId = tenant, LineageKey = "lineage",
            Decision = index == 7 ? KycScreeningDecision.HIT : KycScreeningDecision.NO_HIT,
            PolicyVersion = "policy-1", ScreenedAt = now.AddSeconds(index), CreatedDate = now, RetentionExpiresAt = now.AddMinutes(-1),
        }));
        (await Task.WhenAll(appends)).Should().OnlyContain(result => !result.IsError);
        var guard = await _executor.QuerySingleAsync<KycAuthorityGuard>(SurrealQuery<KycAuthorityGuard>.Key(KycEvidenceFence.GuardId(user, tenant)));
        guard!.AuthorityRevision.Should().Be(8);
        (await screening.GetLatestResultAsync(Guid.ParseExact(user, "N"), Guid.ParseExact(tenant, "N"), lineageKey: "lineage")).Result!.Decision.Should().Be(KycScreeningDecision.HIT);
        await screening.SaveResultAsync(new KycScreeningResult { Id = Guid.NewGuid().ToString("N"), Decision = KycScreeningDecision.NO_HIT,
            PolicyVersion = "operator-query", ScreenedAt = now, CreatedDate = now });
        (await _executor.QueryAsync<KycAuthorityGuard>(SurrealQuery<KycAuthorityGuard>.From())).Should().ContainSingle();
        (await screening.EraseRetentionExpiredAsync(now, "Fixture retention", 50)).Result.Should().Be(8);
        var erasedGuard = await _executor.QuerySingleAsync<KycAuthorityGuard>(SurrealQuery<KycAuthorityGuard>.Key(KycEvidenceFence.GuardId(user, tenant)));
        erasedGuard!.AuthorityRevision.Should().BeGreaterThan(guard.AuthorityRevision);
        (await _executor.QueryAsync<KycScreeningResult>(SurrealQuery<KycScreeningResult>.From().Where(row => row.ErasedAt != null))).Should().HaveCount(8);
    }

    [Fact]
    public async Task OnlyOneSendLeaseWins_ExpiryPermitsSameRequestReplay_AndSavedResponseSurvivesLostAttachment()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N"); var user = Guid.NewGuid().ToString("N");
        var session = new HostedSession { Id = id, SubjectUserId = user, ProviderKey = "trusted", FlowKind = "kyc",
            AdmissionKey = "kyc-attempt:" + id, CreationToken = Guid.NewGuid().ToString("N"), State = HostedSessionStates.Claimed, CreatedDate = now, ModifiedDate = now };
        await _sessions.ClaimAdmissionAsync(session);
        var submission = new KycSubmission { Id = id, UserId = user, ProviderKey = "trusted", Provider = KycProvider.GENERIC_HOSTED,
            ProviderSessionId = id, HostedVerification = true, Status = KycStatus.PENDING, ExpiresAt = now.AddHours(1), CreatedDate = now, ModifiedDate = now };
        (await _executor.ExecuteAsync(SurrealWriter.Create(submission))).EnsureAllOk();
        var attempt = new HostedProviderAttempt { Id = id, SubjectId = user, ProviderKey = "trusted", AdapterKey = "generic-hosted",
            Origin = "https://verify.example", ProtocolVersion = "1", PolicyRevision = "policy-1", RequestJson = "{\"frozen\":true}",
            RequestDigest = HostedIdentityProtocol.Digest("{\"frozen\":true}"), CreatedAt = now, NextAttemptAt = now };
        await _attempts.EnsureAsync(attempt, default);
        (await _attempts.ClaimAsync(session, true, now, default)).Should().BeNull("prepared evidence cannot authorize first egress");
        (await _attempts.GetAsync(id, default))!.SendStartedAt.Should().BeNull();
        session = (await _sessions.TryBeginCreationAsync(Guid.ParseExact(id, "N"), session.CreationToken!, now)).Result!;
        session.Should().NotBeNull();
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _attempts.ClaimAsync(session, false, now, default)));
        claims.Count(claim => claim is not null).Should().Be(1);
        (await _attempts.ClaimAsync(session, true, now, default)).Should().BeNull();
        var winner = claims.Single(claim => claim is not null)!;
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<HostedProviderAttempt>(id)
            .Set(row => row.LeaseExpiresAt, now.AddMinutes(-1)).Build())).EnsureAllOk();
        var resumed = await _attempts.ClaimAsync(session, true, now, default);
        resumed.Should().NotBeNull(); resumed!.RequestJson.Should().Be(attempt.RequestJson);
        var creation = new HostedSessionCreation { ProviderSessionId = "remote", RedirectUrl = "https://verify.example/visit" };
        (await _attempts.SaveCreationAsync(winner, creation, default)).Should().BeNull();
        (await _attempts.SaveCreationAsync(resumed, creation, default)).Should().NotBeNull();
        (await _attempts.ListDueAsync(now.AddMinutes(3), default)).Should().Contain(row => row.Id == id && row.RemoteSessionId == "remote");
        (await _sessions.GetByIdAsync(Guid.ParseExact(id, "N"))).Result!.ProviderSessionId.Should().BeNull();
    }

    [Fact]
    public async Task ConflictingTerminalEvidencePermanentlyFencesUnappliedApproval_AndCallbacksBindExactPayload()
    {
        var id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow;
        var session = new HostedSession { Id = id, ProviderKey = "trusted", FlowKind = "kyc", AdmissionKey = id,
            State = HostedSessionStates.Resuming, ProviderSessionId = "remote", CreationToken = "creator", ResumeLeaseToken = "lease",
            ResumeLeaseExpiresAt = now.AddMinutes(1), CreatedDate = now, ModifiedDate = now };
        (await _executor.ExecuteAsync(SurrealWriter.Create(session))).EnsureAllOk();
        (await _attempts.PinTerminalAsync(id, "remote", "decision-a", "digest-a", default)).Should().BeTrue();
        (await _attempts.PinTerminalAsync(id, "remote", "decision-b", "digest-b", default)).Should().BeFalse();
        (await _attempts.PinTerminalAsync(id, "remote", "decision-a", "digest-a", default)).Should().BeFalse();
        var stored = await _executor.QuerySingleAsync<HostedSession>(SurrealQuery<HostedSession>.Key(id));
        stored!.ProviderEvidenceConflict.Should().BeTrue(); stored.ProviderDecisionId.Should().Be("decision-a");
        var receipt = new HostedProviderCallback { Id = "delivery", AttemptId = id, BodyDigest = "body-a", ReceivedAt = now };
        (await _attempts.AdmitCallbackAsync(receipt, default)).Should().NotBeNull();
        await _attempts.CompleteCallbackAsync(receipt.Id, default);
        (await _attempts.AdmitCallbackAsync(receipt, default))!.Processed.Should().BeTrue();
        receipt.BodyDigest = "body-b";
        (await _attempts.AdmitCallbackAsync(receipt, default)).Should().BeNull();
    }

    [Fact]
    public async Task SchedulingFailedAttemptsMovesThemBehindLaterDueWork()
    {
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 55; index++) await _attempts.EnsureAsync(new HostedProviderAttempt
        {
            Id = Guid.NewGuid().ToString("N"), AdapterKey = "generic-hosted", ProviderKey = "trusted", Origin = "https://verify.example",
            SubjectId = Guid.NewGuid().ToString("N"), ProtocolVersion = "1", PolicyRevision = "policy-1", RequestJson = "{}",
            RequestDigest = HostedIdentityProtocol.Digest("{}"), CreatedAt = now, NextAttemptAt = now.AddMinutes(-55 + index),
        }, default);
        var first = await _attempts.ListDueAsync(now, default);
        first.Should().HaveCount(50);
        foreach (var attempt in first) await _attempts.ScheduleAsync(attempt.Id, now.AddMinutes(2), false, default);
        var later = await _attempts.ListDueAsync(now, default);
        later.Should().HaveCount(5); later.Select(row => row.Id).Should().NotIntersectWith(first.Select(row => row.Id));
    }
}
