using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentWebhookEvent;
using Registration = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistration;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealConsentLifecycleTests
{
    [Fact]
    public async Task Concurrent_delivery_claims_consume_one_attempt_and_only_exact_holder_can_finish()
    {
        var notification = await DeliverableEvent();
        var store = new SurrealConsentWebhookDeliveryStore(_executor);
        var id = SurrealRecordGuid.BareId(notification.Id);
        var claims = await Task.WhenAll(store.TryClaimAsync(id, 0, TimeSpan.FromMinutes(1), 8),
            store.TryClaimAsync(id, 0, TimeSpan.FromMinutes(1), 8));
        var lease = claims.Where(item => item is not null).Should().ContainSingle().Which!;
        lease.AttemptCount.Should().Be(1);
        lease.CycleAttemptCount.Should().Be(1);
        (await store.FinishAsync(lease with { LeaseId = Guid.NewGuid().ToString("N") },
            WebhookAttemptOutcome.Delivered, TimeSpan.Zero, null)).Should().BeFalse();
        (await store.SuspendUnavailableAsync(id, lease.Version, 1)).Should().BeFalse();
        (await store.FinishAsync(lease, WebhookAttemptOutcome.Delivered, TimeSpan.Zero, null)).Should().BeTrue();
        (await store.FinishAsync(lease, WebhookAttemptOutcome.Retry, TimeSpan.Zero, "HTTP_503")).Should().BeFalse();
        var current = (await _executor.QuerySingleAsync<Event>(SurrealQuery<Event>.Key(id)))!;
        current.Status.Should().Be(Event.DeliveryStatus.Delivered);
        current.AttemptEncryptedSecret.Should().BeNull();
        current.LeaseId.Should().BeNull();
        current.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task Changed_destination_cannot_retarget_original_event_or_audited_retry()
    {
        var notification = await DeliverableEvent();
        var store = new SurrealConsentWebhookDeliveryStore(_executor);
        var id = SurrealRecordGuid.BareId(notification.Id);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<Registration>(notification.RegistrationId!).Where(seed => true)
            .Set(row => row.Version, 2L).Set(row => row.DestinationRevision, 2L)
            .Set(row => row.Url, "https://replacement.example/events").Build())).EnsureAllOk();
        (await store.TryClaimAsync(id, 0, TimeSpan.FromMinutes(1), 8)).Should().BeNull();
        (await store.SuspendUnavailableAsync(id, 0, 8)).Should().BeTrue();
        var command = new WebhookEventRetryCommand(Guid.NewGuid(), 1, "Retry the original notification");
        (await store.RetryAsync(_tenant, Guid.ParseExact(id, "N"), command, DeliveryAuthority())).Should().BeNull();
        var current = (await _executor.QuerySingleAsync<Event>(SurrealQuery<Event>.Key(id)))!;
        current.DestinationUrl.Should().Be(notification.DestinationUrl);
        current.AttemptCount.Should().Be(0);
        current.LastError.Should().Be("ORIGINAL_DESTINATION_UNAVAILABLE");
    }

    [Fact]
    public async Task Retry_response_loss_replays_original_receipt_without_resetting_later_attempts()
    {
        var notification = await DeliverableEvent();
        var store = new SurrealConsentWebhookDeliveryStore(_executor);
        var id = SurrealRecordGuid.BareId(notification.Id);
        var lease = (await store.TryClaimAsync(id, 0, TimeSpan.FromMinutes(1), 1))!;
        (await store.FinishAsync(lease, WebhookAttemptOutcome.DeadLetter, TimeSpan.Zero, "HTTP_503")).Should().BeTrue();
        var command = new WebhookEventRetryCommand(Guid.NewGuid(), 2, "Receiver outage has been resolved");
        var authority = DeliveryAuthority();
        var retries = await Task.WhenAll(store.RetryAsync(_tenant, Guid.ParseExact(id, "N"), command, authority),
            store.RetryAsync(_tenant, Guid.ParseExact(id, "N"), command, authority));
        retries.Should().OnlyContain(item => item != null);
        retries[0].Should().Be(retries[1]);
        var next = (await store.TryClaimAsync(id, retries[0]!.Version, TimeSpan.FromMinutes(1), 1))!;
        next.AttemptCount.Should().Be(2); next.CycleAttemptCount.Should().Be(1);
        (await store.RetryAsync(_tenant, Guid.ParseExact(id, "N"), command, authority)).Should().Be(retries[0]);
        (await store.GetAsync(Guid.NewGuid(), Guid.ParseExact(id, "N"))).Should().BeNull();
        (await store.GetAsync(_tenant, Guid.ParseExact(id, "N")))!.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task Tampered_body_is_suspended_without_consuming_an_http_attempt()
    {
        var notification = await DeliverableEvent();
        var id = SurrealRecordGuid.BareId(notification.Id);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<Event>(id).Where(seed => true).Set(row => row.BodyJson, "{}").Build())).EnsureAllOk();
        var store = new SurrealConsentWebhookDeliveryStore(_executor);
        (await store.TryClaimAsync(id, 0, TimeSpan.FromMinutes(1), 8)).Should().BeNull();
        (await store.SuspendUnavailableAsync(id, 0, 8)).Should().BeTrue();
        var current = (await _executor.QuerySingleAsync<Event>(SurrealQuery<Event>.Key(id)))!;
        current.LastError.Should().Be("ACCEPTANCE_EVIDENCE_MISSING");
        current.AttemptCount.Should().Be(0);
    }

    private SigningConsentWitness DeliveryAuthority()
        => new(1, Authority() with { ActorUserId = _tenant }, [AzoaScopes.TenantProvision], []);

    private async Task<Event> DeliverableEvent()
    {
        (await _executor.ExecuteAsync(SurrealWriter.Create(new Registration { Id = Guid.NewGuid().ToString("N"),
            TenantId = "user:" + _tenant.ToString("N"), Url = "https://receiver.example/events", Version = 1,
            DestinationRevision = 1, SecretRevision = 1, EncryptedSecret = "store-fixture-protected-secret",
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow }))).EnsureAllOk();
        var command = new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, [AzoaScopes.ActionExecute], GrantOrigin.UserExplicit, null, null);
        (await _store.AdmitUserExplicitAsync(command, Authority())).Should().NotBeNull();
        return (await _executor.QueryAsync<Event>(SurrealQuery<Event>.From())).Should().ContainSingle().Which;
    }
}
