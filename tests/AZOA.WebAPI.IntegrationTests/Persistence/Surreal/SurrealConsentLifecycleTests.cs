using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using Grant = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentWebhookEvent;
using User = AZOA.WebAPI.Persistence.SurrealDb.Models.User;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealConsentLifecycleTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private readonly Guid _actor = Guid.NewGuid(), _tenant = Guid.NewGuid();
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealConsentLifecycleStore _store = null!;
    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "consent_grant", "consent_audit", "consent_webhook_event",
            "consent_lifecycle_command", "consent_lifecycle_transition", "consent_participation_boundary", "consent_participation_approval", "webhook_registration", "webhook_event_retry_audit", "wallet", "local_entity_mutation_lock", "kyc_submission", "kyc_document", "hosted_session",
            "tenant_kyc_provider_selection", "kyc_provider_profile");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection); _store = new(_executor);
        foreach (var id in new[] { _actor, _tenant })
            (await _executor.ExecuteAsync(SurrealWriter.Create(new User { Id = id.ToString("N"), Username = id.ToString("N"),
                Email = id + "@test.invalid", PasswordHash = "test", IsActive = true }))).EnsureAllOk();
    }
    public async Task DisposeAsync() { _connection?.Dispose(); await SurrealTestSchema.DropAsync(_namespace); }

    [Fact]
    public async Task Concurrent_command_creates_one_grant_audit_event_and_original_receipt()
    {
        var command = new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, [AzoaScopes.ActionExecute], GrantOrigin.UserExplicit, null, null);
        var authority = Authority();
        var results = await Task.WhenAll(_store.AdmitUserExplicitAsync(command, authority), _store.AdmitUserExplicitAsync(command, authority));
        results.Should().OnlyContain(result => result != null);
        results[0]!.GrantId.Should().Be(results[1]!.GrantId);
        (await _executor.QueryAsync<Grant>(SurrealQuery<Grant>.From())).Should().ContainSingle();
        (await _executor.QueryAsync<ConsentAudit>(SurrealQuery<ConsentAudit>.From())).Should().ContainSingle();
        var notification = (await _executor.QueryAsync<Event>(SurrealQuery<Event>.From())).Should().ContainSingle().Which;
        notification.Status.Should().Be(Event.DeliveryStatus.DeadLettered);
        notification.LastError.Should().Be("DESTINATION_UNAVAILABLE_AT_TRANSITION");
        notification.BodyJson.Should().NotBeNullOrWhiteSpace();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<Grant>(results[0]!.GrantId.ToString("N")).Where(seed => true)
            .Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build())).EnsureAllOk();
        (await _store.AdmitUserExplicitAsync(command, authority))!.GrantedAt.Should().Be(results[0]!.GrantedAt);
        (await _executor.QueryAsync<Grant>(SurrealQuery<Grant>.From())).Should().ContainSingle().Which.RevokedAt.Should().NotBeNull();
        (await _store.AdmitUserExplicitAsync(command with { Scopes = ["different:scope"] }, authority)).Should().BeNull();
    }

    [Fact]
    public async Task Revoked_actor_authority_leaves_no_partial_lifecycle_records()
    {
        var authority = Authority();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N")).Where(seed => true)
            .Set(row => row.AuthNotBefore, DateTimeOffset.UtcNow).Build())).EnsureAllOk();
        var command = new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, [AzoaScopes.ActionExecute], GrantOrigin.UserExplicit, null, null);
        (await _store.AdmitUserExplicitAsync(command, authority)).Should().BeNull();
        (await _executor.QueryAsync<Grant>(SurrealQuery<Grant>.From())).Should().BeEmpty();
        (await _executor.QueryAsync<ConsentAudit>(SurrealQuery<ConsentAudit>.From())).Should().BeEmpty();
        (await _executor.QueryAsync<Event>(SurrealQuery<Event>.From())).Should().BeEmpty();
        (await _executor.QueryAsync<ConsentLifecycleCommandReceipt>(SurrealQuery<ConsentLifecycleCommandReceipt>.From())).Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_revocation_commits_one_terminal_time_and_one_notification()
    {
        var authority = Authority();
        var command = new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, [AzoaScopes.ActionExecute], GrantOrigin.UserExplicit, null, null);
        var admission = (await _store.AdmitUserExplicitAsync(command, authority))!;
        var results = await Task.WhenAll(_store.RevokeUserExplicitAsync(admission.GrantId, authority),
            _store.RevokeUserExplicitAsync(admission.GrantId, authority));
        results.Should().OnlyContain(result => result != null && result.NotificationRecorded);
        results[0]!.RevokedAt.Should().Be(results[1]!.RevokedAt);
        (await _executor.QueryAsync<ConsentLifecycleTransition>(SurrealQuery<ConsentLifecycleTransition>.From().Where(row => row.Kind == "Revoked")))
            .Should().ContainSingle().Which.OccurredAt.Should().Be(results[0]!.RevokedAt);
        (await _executor.QueryAsync<Event>(SurrealQuery<Event>.From().Where(row => row.EventType == Event.WebhookEventType.Revoked)))
            .Should().ContainSingle().Which.OccurredAt.Should().Be(results[0]!.RevokedAt);
        (await _store.RevokeUserExplicitAsync(admission.GrantId, authority)).Should().Be(results[0]);
        (await _store.AdmitUserExplicitAsync(command, authority))!.GrantId.Should().Be(admission.GrantId);
        (await _executor.QuerySingleAsync<Grant>(SurrealQuery<Grant>.Key(admission.GrantId.ToString("N"))))!
            .RevokedAt.Should().Be(results[0]!.RevokedAt);
    }

    [Fact]
    public async Task Only_owner_can_revoke_and_inactive_tenant_does_not_block_revocation()
    {
        var authority = Authority();
        var command = new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, [AzoaScopes.ActionExecute], GrantOrigin.UserExplicit, null, null);
        var admission = (await _store.AdmitUserExplicitAsync(command, authority))!;
        (await _store.RevokeUserExplicitAsync(admission.GrantId, authority with { ActorUserId = _tenant })).Should().BeNull();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_tenant.ToString("N")).Where(seed => true)
            .Set(row => row.IsActive, false).Build())).EnsureAllOk();
        var revoked = await _store.RevokeUserExplicitAsync(admission.GrantId, authority);
        revoked.Should().NotBeNull();
        revoked!.NotificationRecorded.Should().BeTrue();
        (await _executor.QueryAsync<Event>(SurrealQuery<Event>.From().Where(row => row.EventType == Event.WebhookEventType.Revoked)))
            .Should().ContainSingle().Which.Status.Should().Be(Event.DeliveryStatus.DeadLettered);
    }

    [Fact]
    public async Task Expiry_and_revoke_race_preserves_original_times_and_never_reactivates_legacy_grant()
    {
        var id = Guid.NewGuid();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(-1);
        (await _executor.ExecuteAsync(SurrealWriter.Create(new Grant { Id = id.ToString("N"),
            GrantorUserId = "user:" + _actor.ToString("N"), TenantId = "user:" + _tenant.ToString("N"),
            Scopes = AzoaScopes.ActionExecute, Origin = "UserExplicit", GrantedAt = expiry.AddDays(-1), ExpiresAt = expiry }))).EnsureAllOk();
        var expiring = _store.RecordExpiryAsync(id);
        var revoking = _store.RevokeUserExplicitAsync(id, Authority());
        await Task.WhenAll(expiring, revoking);
        var revoked = revoking.Result ?? await _store.RevokeUserExplicitAsync(id, Authority());
        revoked.Should().NotBeNull();
        var current = (await _executor.QuerySingleAsync<Grant>(SurrealQuery<Grant>.Key(id.ToString("N"))))!;
        current.RevokedAt.Should().NotBeNull();
        current.ExpiresAt.Should().Be(expiry);
        var notifications = await _executor.QueryAsync<Event>(SurrealQuery<Event>.From());
        notifications.Count(row => row.EventType == Event.WebhookEventType.Revoked).Should().Be(1);
        notifications.Count(row => row.EventType == Event.WebhookEventType.Expired).Should().BeLessThanOrEqualTo(1);
        notifications.Where(row => row.EventType == Event.WebhookEventType.Expired).Should()
            .AllSatisfy(row => row.OccurredAt.Should().Be(expiry));
        (await _store.RecordExpiryAsync(id)).Should().BeFalse();
    }

    [Fact]
    public async Task Closed_membership_refuses_new_participation_but_replays_original_receipt()
    {
        await ApproveReference("membership-one");
        var command = new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, [AzoaScopes.ActionExecute],
            GrantOrigin.Participation, "membership-one", null);
        var receipt = await _store.AdmitParticipationAsync(command, Authority());
        receipt.Should().NotBeNull();
        receipt!.Scopes.Should().Contain(AzoaScopes.ParticipationInboundCreditFloor);
        var membershipId = ConsentLifecycleIdentity.ParticipationId(_tenant, command.ParticipationRef!);
        var membership = (await _executor.QuerySingleAsync<ConsentParticipationBoundary>(SurrealQuery<ConsentParticipationBoundary>.Key(membershipId)))!;
        membership.Version.Should().Be(1);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<ConsentParticipationBoundary>(membershipId).Where(seed => true)
            .Set(row => row.ClosedAt, DateTimeOffset.UtcNow).Set(row => row.Version, 2L).Build())).EnsureAllOk();
        (await _store.AdmitParticipationAsync(command with { RequestId = Guid.NewGuid() }, Authority())).Should().BeNull();
        (await _store.AdmitParticipationAsync(command, Authority()))!.GrantId.Should().Be(receipt.GrantId);
        (await _executor.QueryAsync<Grant>(SurrealQuery<Grant>.From())).Should().ContainSingle();
    }

    [Fact]
    public async Task Each_fresh_participation_admission_advances_the_membership_epoch()
    {
        await ApproveReference("membership-two");
        var command = new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, [AzoaScopes.ActionExecute],
            GrantOrigin.Participation, "membership-two", null);
        (await _store.AdmitParticipationAsync(command, Authority())).Should().NotBeNull();
        (await _store.AdmitParticipationAsync(command with { RequestId = Guid.NewGuid() }, Authority())).Should().NotBeNull();
        var membershipId = ConsentLifecycleIdentity.ParticipationId(_tenant, command.ParticipationRef!);
        (await _executor.QuerySingleAsync<ConsentParticipationBoundary>(SurrealQuery<ConsentParticipationBoundary>.Key(membershipId)))!
            .Version.Should().Be(2);
    }

    [Fact]
    public async Task Offboard_closes_empty_membership_and_replays_original_zero_count()
    {
        var authority = new SigningConsentWitness(1, Authority() with { ActorUserId = _tenant }, [AzoaScopes.TenantProvision], []);
        var closed = await _store.OffboardAsync(_tenant, "never-joined", authority);
        closed.Should().NotBeNull();
        closed!.RevokedCount.Should().Be(0);
        var command = new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, [AzoaScopes.ActionExecute],
            GrantOrigin.Participation, "never-joined", null);
        (await _store.AdmitParticipationAsync(command, Authority())).Should().BeNull();
        (await _store.OffboardAsync(_tenant, "never-joined", authority)).Should().Be(closed);
        (await _executor.QueryAsync<Grant>(SurrealQuery<Grant>.From())).Should().BeEmpty();
    }

    [Fact]
    public async Task Offboard_racing_fresh_admission_leaves_no_live_matching_grant_after_retry()
    {
        await ApproveReference("offboard-race");
        var command = new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, [AzoaScopes.ActionExecute],
            GrantOrigin.Participation, "offboard-race", null);
        (await _store.AdmitParticipationAsync(command, Authority())).Should().NotBeNull();
        var authority = new SigningConsentWitness(1, Authority() with { ActorUserId = _tenant }, [AzoaScopes.TenantProvision], []);
        var admission = _store.AdmitParticipationAsync(command with { RequestId = Guid.NewGuid() }, Authority());
        var closure = _store.OffboardAsync(_tenant, command.ParticipationRef!, authority);
        await Task.WhenAll(admission, closure);
        var closed = closure.Result ?? await _store.OffboardAsync(_tenant, command.ParticipationRef!, authority);
        closed.Should().NotBeNull();
        var grants = await _executor.QueryAsync<Grant>(SurrealQuery<Grant>.From());
        grants.Should().OnlyContain(row => row.RevokedAt == closed!.ClosedAt);
        closed!.RevokedCount.Should().Be(grants.Count);
        var events = await _executor.QueryAsync<Event>(SurrealQuery<Event>.From().Where(row => row.EventType == Event.WebhookEventType.Revoked));
        events.Count.Should().Be(grants.Count);
        events.Should().OnlyContain(row => row.OccurredAt == closed.ClosedAt);
        (await _store.OffboardAsync(_tenant, command.ParticipationRef!, authority)).Should().Be(closed);
        (await _store.AdmitParticipationAsync(command with { RequestId = Guid.NewGuid() }, Authority())).Should().BeNull();
    }

    [Fact]
    public async Task User_cannot_self_enroll_without_tenant_approval()
    {
        var command = new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, [AzoaScopes.ActionExecute],
            GrantOrigin.Participation, "invented-membership", null);
        (await _store.AdmitParticipationAsync(command, Authority())).Should().BeNull();
        (await _executor.QueryAsync<Grant>(SurrealQuery<Grant>.From())).Should().BeEmpty();
        (await _executor.QueryAsync<ConsentParticipationBoundary>(SurrealQuery<ConsentParticipationBoundary>.From())).Should().BeEmpty();
    }

    [Fact]
    public async Task Provisioning_uses_actual_tenant_authority_and_cannot_create_grant_after_claim()
    {
        var authority = new SigningConsentWitness(1, Authority() with { ActorUserId = _tenant }, [AzoaScopes.TenantProvision], []);
        (await _store.AdmitProvisionedAsync(_actor, "child", authority)).Should().BeNull();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N")).Where(seed => true)
            .Set(row => row.OwnerTenantId, "user:" + _tenant.ToString("N")).Set(row => row.ExternalUserId, "child").Build())).EnsureAllOk();
        var result = await _store.AdmitProvisionedAsync(_actor, "child", authority);
        result.Should().NotBeNull();
        result!.GrantorUserId.Should().Be(_actor);
        (await _executor.QueryAsync<ConsentParticipationApproval>(SurrealQuery<ConsentParticipationApproval>.From()))
            .Should().ContainSingle().Which.GrantorId.Should().Be(_actor.ToString("N"));
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N")).Where(seed => true)
            .Unset(row => row.OwnerTenantId).Set(row => row.AuthNotBefore, DateTimeOffset.UtcNow).Build())).EnsureAllOk();
        (await _store.AdmitProvisionedAsync(_actor, "child", authority))!.GrantId.Should().Be(result.GrantId);
        (await _store.AdmitProvisionedAsync(_actor, "another-child", authority)).Should().BeNull();
        (await _executor.QueryAsync<Grant>(SurrealQuery<Grant>.From())).Should().ContainSingle();
    }

    [Fact]
    public async Task Provisioning_issuer_concurrency_commits_one_original_grant_and_never_regrants_after_revocation()
    {
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N")).Where(seed => true)
            .Set(row => row.OwnerTenantId, "user:" + _tenant.ToString("N")).Set(row => row.ExternalUserId, "child").Build())).EnsureAllOk();
        var issuer = new AZOA.WebAPI.Services.Consent.TenantProvisionedGrantIssuer(_store);
        var authority = new SigningConsentWitness(1, Authority() with { ActorUserId = _tenant }, [AzoaScopes.TenantProvision], []);
        var results = await Task.WhenAll(issuer.IssueOnProvisionAsync(_actor, "child", authority),
            issuer.IssueOnProvisionAsync(_actor, "child", authority));
        results.Should().OnlyContain(result => !result.IsError && result.Result != null);
        var receipt = results[0].Result!;
        results[1].Result!.GrantId.Should().Be(receipt.GrantId);
        (await _store.RevokeUserExplicitAsync(receipt.GrantId, Authority())).Should().NotBeNull();
        var replay = await issuer.IssueOnProvisionAsync(_actor, "child", authority);
        replay.Result!.GrantId.Should().Be(receipt.GrantId);
        (await _executor.QueryAsync<Grant>(SurrealQuery<Grant>.From())).Should().ContainSingle().Which.RevokedAt.Should().NotBeNull();
        (await _executor.QueryAsync<Event>(SurrealQuery<Event>.From().Where(row => row.EventType == Event.WebhookEventType.Granted)))
            .Should().ContainSingle();
    }

    private async Task ApproveReference(string reference)
    {
        var membershipId = ConsentLifecycleIdentity.ParticipationId(_tenant, reference);
        (await _executor.ExecuteAsync(SurrealWriter.Create(new ConsentParticipationBoundary { Id = membershipId,
            TenantId = _tenant.ToString("N"), ParticipationRef = reference, CreatedAt = DateTimeOffset.UtcNow }))).EnsureAllOk();
        (await _executor.ExecuteAsync(SurrealWriter.Create(new ConsentParticipationApproval {
            Id = ConsentLifecycleIdentity.ApprovalId(membershipId, _actor), MembershipId = membershipId,
            TenantId = _tenant.ToString("N"), GrantorId = _actor.ToString("N"), IsActive = true,
            SourceKind = "TenantProvisioned", SourceCommandId = "fixture-approved-enrollment", CreatedAt = DateTimeOffset.UtcNow }))).EnsureAllOk();
    }

    private SigningCredentialWitness Authority() => new(1, "Jwt", _actor, null, null,
        DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddHours(1), null, null, [], true, TokenUse: AzoaClaims.TokenUseLogin);
}
