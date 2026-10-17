using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Services.Webhooks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionWebhookEvent;
using Registration = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistration;
using WebhookAttemptOutcome = AZOA.WebAPI.Models.WebhookAttemptOutcome;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealActionEmitTests
{
    [Fact]
    public async Task Delivery_claim_has_one_winner_and_pins_secret_across_rotation()
    {
        var (eventId, registration, protector) = await DeliveryEvent();
        var deliveries = new SurrealActionWebhookDeliveryStore(_executor, _store);
        var contenders = await Task.WhenAll(deliveries.TryClaimAsync(eventId, 0, TimeSpan.FromMinutes(1), 8),
            deliveries.TryClaimAsync(eventId, 0, TimeSpan.FromMinutes(1), 8));
        var lease = contenders.Where(item => item is not null).Should().ContainSingle().Subject!;
        JsonSerializer.Serialize(lease).Should().NotContain(lease.EncryptedSecret);
        var original = protector.Unprotect(lease.TenantId, lease.RegistrationId, lease.SecretRevision, lease.EncryptedSecret);
        var replacement = protector.Protect(lease.TenantId, lease.RegistrationId, 2, WebhookSecretProtector.GenerateSecret());
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<Registration>(registration.Id)
            .Set(row => row.Version, 2L).Set(row => row.SecretRevision, 2L).Set(row => row.EncryptedSecret, replacement).Build())).EnsureAllOk();
        protector.Unprotect(lease.TenantId, lease.RegistrationId, lease.SecretRevision, lease.EncryptedSecret).Should().Be(original);
        (await deliveries.FinishAsync(lease, WebhookAttemptOutcome.Retry, TimeSpan.Zero, "HTTP_503")).Should().BeTrue();
        var next = await deliveries.TryClaimAsync(eventId, 2, TimeSpan.FromMinutes(1), 8);
        next.Should().NotBeNull();
        next!.SecretRevision.Should().Be(2);
        next.EncryptedSecret.Should().Be(replacement);
        next.BodyJson.Should().Be(lease.BodyJson);
        next.IdempotencyId.Should().Be(lease.IdempotencyId);
        (await deliveries.FinishAsync(lease, WebhookAttemptOutcome.Delivered, TimeSpan.Zero, null)).Should().BeFalse();
        (await deliveries.FinishAsync(next, WebhookAttemptOutcome.Delivered, TimeSpan.Zero, null)).Should().BeTrue();
        var saved = (await _executor.QuerySingleAsync<Event>(SurrealQuery<Event>.Key(eventId)))!;
        saved.AttemptEncryptedSecret.Should().BeNull();
        saved.LeaseId.Should().BeNull();
        saved.Status.Should().Be(Event.DeliveryStatus.Delivered);
    }

    [Fact]
    public async Task Disable_after_authorization_does_not_recall_inflight_attempt_but_blocks_next_claim()
    {
        var (eventId, registration, _) = await DeliveryEvent();
        var deliveries = new SurrealActionWebhookDeliveryStore(_executor, _store);
        var lease = (await deliveries.TryClaimAsync(eventId, 0, TimeSpan.FromMinutes(1), 8))!;
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<Registration>(registration.Id)
            .Set(row => row.Version, 2L).Set(row => row.IsActive, false).Build())).EnsureAllOk();
        (await deliveries.SuspendUnavailableAsync(eventId, lease.Version, 8)).Should().BeFalse();
        (await deliveries.FinishAsync(lease, WebhookAttemptOutcome.Retry, TimeSpan.Zero, "HTTP_503")).Should().BeTrue();
        (await deliveries.TryClaimAsync(eventId, 2, TimeSpan.FromMinutes(1), 8)).Should().BeNull();
        (await deliveries.SuspendUnavailableAsync(eventId, 2, 8)).Should().BeTrue();
        var saved = (await _executor.QuerySingleAsync<Event>(SurrealQuery<Event>.Key(eventId)))!;
        saved.LastError.Should().Be("ORIGINAL_DESTINATION_UNAVAILABLE");
        saved.Status.Should().Be(Event.DeliveryStatus.DeadLettered);
        saved.DestinationUrl.Should().Be(registration.Url);
    }

    [Fact]
    public async Task Exhausted_abandoned_attempt_is_retired_and_secret_is_removed()
    {
        var (eventId, _, _) = await DeliveryEvent();
        var deliveries = new SurrealActionWebhookDeliveryStore(_executor, _store);
        var lease = (await deliveries.TryClaimAsync(eventId, 0, TimeSpan.FromMinutes(1), 1))!;
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<Event>(eventId)
            .Set(row => row.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)).Build())).EnsureAllOk();
        (await deliveries.TryClaimAsync(eventId, 1, TimeSpan.FromMinutes(1), 1)).Should().BeNull();
        (await deliveries.SuspendUnavailableAsync(eventId, 1, 1)).Should().BeTrue();
        (await deliveries.FinishAsync(lease, WebhookAttemptOutcome.Delivered, TimeSpan.Zero, null)).Should().BeFalse();
        var saved = (await _executor.QuerySingleAsync<Event>(SurrealQuery<Event>.Key(eventId)))!;
        saved.LastError.Should().Be("ATTEMPTS_EXHAUSTED");
        saved.AttemptEncryptedSecret.Should().BeNull();
    }

    private async Task<(string EventId, Registration Registration, WebhookSecretProtector Protector)> DeliveryEvent()
    {
        var tenant = Guid.NewGuid();
        await Save(new User { Id = tenant.ToString("N"), Username = tenant.ToString("N"), Email = tenant + "@test.invalid",
            PasswordHash = "test", IsActive = true });
        var grant = new ConsentGrant { Id = Guid.NewGuid().ToString("N"), GrantorUserId = "user:" + _actor.ToString("N"),
            TenantId = "user:" + tenant.ToString("N"), Scopes = AzoaScopes.ActionExecute, GrantedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await Save(grant);
        var (input, authority) = await Command(tenant, new SigningGrantWitness(Guid.ParseExact(grant.Id, "N"),
            AzoaScopes.ActionExecute, grant.Scopes, grant.GrantedAt, null));
        var protector = new WebhookSecretProtector(new WalletKeyService(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AZOA:WalletEncryptionKey"] = "delivery-fixture-key" }).Build()));
        var registrationId = Guid.NewGuid();
        var registration = new Registration { Id = registrationId.ToString("N"), TenantId = "user:" + tenant.ToString("N"),
            Url = "https://receiver.example/events", IsActive = true, Version = 1, DestinationRevision = 1, SecretRevision = 1,
            EncryptedSecret = protector.Protect(tenant, registrationId, 1, WebhookSecretProtector.GenerateSecret()), CreatedAt = DateTimeOffset.UtcNow };
        await Save(registration);
        var receipt = (await _store.AcceptAsync(input, authority))!;
        return (receipt.EventId!, registration, protector);
    }
}
