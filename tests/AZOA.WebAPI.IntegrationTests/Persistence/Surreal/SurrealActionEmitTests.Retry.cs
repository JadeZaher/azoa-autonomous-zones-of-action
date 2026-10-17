using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
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
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<Registration>(registration.Id)
            .Set(row => row.Version, 2L).Set(row => row.DestinationRevision, 2L)
            .Set(row => row.Url, "https://replacement.example/events").Build())).EnsureAllOk();
        (await events.RetryAsync(tenant, Guid.ParseExact(eventId, "N"), command, TenantManagement(tenant))).Should().BeNull();
        (await _executor.QueryAsync<Audit>(SurrealQuery<Audit>.From())).Should().BeEmpty();
        var original = (await _executor.QuerySingleAsync<Event>(SurrealQuery<Event>.Key(eventId)))!;
        original.Status.Should().Be(Event.DeliveryStatus.DeadLettered);
        original.DestinationUrl.Should().Be(registration.Url);
    }

    private static SigningConsentWitness TenantManagement(Guid tenant)
        => new(1, new SigningCredentialWitness(1, "Jwt", tenant, null, null, DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1), null, null, [AzoaScopes.TenantProvision], true, TokenUse: AzoaClaims.TokenUseLogin),
            [AzoaScopes.TenantProvision], []);
}
