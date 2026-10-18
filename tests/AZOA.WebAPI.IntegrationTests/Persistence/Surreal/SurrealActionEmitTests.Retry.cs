using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Webhooks;
using FluentAssertions;
using Microsoft.Extensions.Options;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionWebhookEvent;
using Registration = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistration;
using Audit = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookEventRetryAudit;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealActionEmitTests
{
    [Fact]
    public async Task Retry_is_audited_once_and_preserves_lifetime_attempts_and_original_event()
    {
        var (eventId, registration, _) = await DeliveryEvent();
        var tenant = SurrealRecordGuid.Parse(registration.TenantId);
        var events = new SurrealActionWebhookDeliveryStore(_executor, _store);
        var first = (await events.TryClaimAsync(eventId, 0, TimeSpan.FromMinutes(1), 1))!;
        (await events.FinishAsync(first, WebhookAttemptOutcome.DeadLetter, TimeSpan.Zero, "HTTP_503")).Should().BeTrue();
        var command = new WebhookEventRetryCommand(Guid.NewGuid(), 2, "Receiver was repaired and is ready.");
        var results = await Task.WhenAll(events.RetryAsync(tenant, Guid.ParseExact(eventId, "N"), command, TenantManagement(tenant)),
            events.RetryAsync(tenant, Guid.ParseExact(eventId, "N"), command, TenantManagement(tenant)));
        results.Should().OnlyContain(result => result != null && result.Version == 3 && result.RetryGeneration == 1);
        (await _executor.QueryAsync<Audit>(SurrealQuery<Audit>.From())).Should().ContainSingle();
        var next = (await events.TryClaimAsync(eventId, 3, TimeSpan.FromMinutes(1), 1))!;
        next.AttemptCount.Should().Be(2); next.CycleAttemptCount.Should().Be(1);
        next.BodyJson.Should().Be(first.BodyJson); next.IdempotencyId.Should().Be(first.IdempotencyId);
        var replay = await events.RetryAsync(tenant, Guid.ParseExact(eventId, "N"), command, TenantManagement(tenant));
        replay.Should().Be(results[0]);
        (await events.RetryAsync(tenant, Guid.ParseExact(eventId, "N"), command with { Reason = "A changed reason must conflict." }, TenantManagement(tenant))).Should().BeNull();
        var status = (await events.GetAsync(tenant, Guid.ParseExact(eventId, "N")))!;
        var json = JsonSerializer.Serialize(status);
        json.Should().NotContain(first.BodyJson).And.NotContain(first.EncryptedSecret).And.NotContain("EncryptedSecret");
        status.AttemptCount.Should().Be(2); status.RetryGeneration.Should().Be(1);
    }

    [Fact]
    public async Task Replacement_destination_and_other_tenant_cannot_take_over_event()
    {
        var (eventId, registration, _) = await DeliveryEvent();
        var tenant = SurrealRecordGuid.Parse(registration.TenantId);
        var events = new SurrealActionWebhookDeliveryStore(_executor, _store);
        var lease = (await events.TryClaimAsync(eventId, 0, TimeSpan.FromMinutes(1), 1))!;
        await events.FinishAsync(lease, WebhookAttemptOutcome.DeadLetter, TimeSpan.Zero, "HTTP_503");
        var command = new WebhookEventRetryCommand(Guid.NewGuid(), 2, "Request another delivery attempt.");
        var other = Guid.NewGuid();
        (await events.GetAsync(other, Guid.ParseExact(eventId, "N"))).Should().BeNull();
        (await events.ListAsync(other, 0, 25)).Should().BeEmpty();
        (await events.RetryAsync(tenant, Guid.ParseExact(eventId, "N"), command, TenantManagement(other))).Should().BeNull();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<Registration>(registration.Id).Where(seed => true)
            .Set(row => row.Version, 2L).Set(row => row.DestinationRevision, 2L)
            .Set(row => row.Url, "https://replacement.example/events").Build())).EnsureAllOk();
        (await events.RetryAsync(tenant, Guid.ParseExact(eventId, "N"), command, TenantManagement(tenant))).Should().BeNull();
        (await _executor.QueryAsync<Audit>(SurrealQuery<Audit>.From())).Should().BeEmpty();
        var original = (await _executor.QuerySingleAsync<Event>(SurrealQuery<Event>.Key(eventId)))!;
        original.Status.Should().Be(Event.DeliveryStatus.DeadLettered);
        original.DestinationUrl.Should().Be(registration.Url);
    }

    [Fact]
    public async Task Exhausted_pending_cycle_is_retryable_but_a_live_attempt_is_not()
    {
        var (eventId, registration, _) = await DeliveryEvent();
        var tenant = SurrealRecordGuid.Parse(registration.TenantId);
        var events = new SurrealActionWebhookDeliveryStore(_executor, _store, Options.Create(new WebhookOptions { MaxAttempts = 1 }));
        var live = (await events.TryClaimAsync(eventId, 0, TimeSpan.FromMinutes(1), 1))!;
        (await events.GetAsync(tenant, Guid.ParseExact(eventId, "N")))!.Retryable.Should().BeFalse();
        (await events.RetryAsync(tenant, Guid.ParseExact(eventId, "N"),
            new WebhookEventRetryCommand(Guid.NewGuid(), live.Version, "Retry while the attempt is in flight."), TenantManagement(tenant))).Should().BeNull();

        // The holder reported a retryable failure, but the worker never suspended the spent cycle.
        (await events.FinishAsync(live, WebhookAttemptOutcome.Retry, TimeSpan.Zero, "HTTP_503")).Should().BeTrue();
        (await events.TryClaimAsync(eventId, live.Version + 1, TimeSpan.FromMinutes(1), 1)).Should().BeNull();
        (await events.GetAsync(tenant, Guid.ParseExact(eventId, "N")))!.Retryable.Should().BeTrue();
        var receipt = await events.RetryAsync(tenant, Guid.ParseExact(eventId, "N"),
            new WebhookEventRetryCommand(Guid.NewGuid(), live.Version + 1, "Receiver repaired; spend a new cycle."), TenantManagement(tenant));
        receipt!.RetriedFrom.Should().Be("AttemptBudgetExhausted");
        var next = (await events.TryClaimAsync(eventId, receipt.Version, TimeSpan.FromMinutes(1), 1))!;
        next.CycleAttemptCount.Should().Be(1);
        next.AttemptCount.Should().Be(2);
        (await events.FinishAsync(live, WebhookAttemptOutcome.Delivered, TimeSpan.Zero, null)).Should().BeFalse(
            "the superseded holder cannot finish the new cycle");
    }

    private static SigningConsentWitness TenantManagement(Guid tenant)
        => new(1, new SigningCredentialWitness(1, "Jwt", tenant, null, null, DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1), null, null, [AzoaScopes.TenantProvision], true, TokenUse: AzoaClaims.TokenUseLogin),
            [AzoaScopes.TenantProvision], []);
}
