using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using FluentAssertions;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentWebhookEvent;
using Transition = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentLifecycleTransition;

namespace AZOA.WebAPI.Tests.Models;

public sealed class ConsentNotificationEvidenceTests
{
    [Theory]
    [InlineData(ConsentWebhookEventType.Granted)]
    [InlineData(ConsentWebhookEventType.Revoked)]
    [InlineData(ConsentWebhookEventType.Expired)]
    public void Original_transition_matches_and_changed_body_or_destination_refuses(ConsentWebhookEventType kind)
    {
        var (notification, proof) = Fixture(kind);
        ConsentNotificationEvidence.Matches(notification, proof).Should().BeTrue();
        notification.DestinationRevision++;
        ConsentNotificationEvidence.Matches(notification, proof).Should().BeFalse();
        notification.DestinationRevision = proof.DestinationRevision;
        notification.DestinationUrl = "https://replacement.example/events";
        ConsentNotificationEvidence.Matches(notification, proof).Should().BeFalse();
        notification.DestinationUrl = proof.DestinationUrl;
        notification.BodyJson += " ";
        ConsentNotificationEvidence.Matches(notification, proof).Should().BeFalse();
    }

    [Fact]
    public void Changed_subject_or_business_time_cannot_borrow_an_original_proof()
    {
        var (notification, proof) = Fixture(ConsentWebhookEventType.Revoked);
        notification.UserId = Guid.NewGuid().ToString("N");
        ConsentNotificationEvidence.Matches(notification, proof).Should().BeFalse();
        (notification, proof) = Fixture(ConsentWebhookEventType.Revoked);
        notification.OccurredAt = notification.OccurredAt.AddSeconds(1);
        ConsentNotificationEvidence.Matches(notification, proof).Should().BeFalse();
    }

    [Fact]
    public void Malformed_original_receipt_and_legacy_missing_evidence_refuse()
    {
        var (notification, proof) = Fixture(ConsentWebhookEventType.Granted);
        proof.GrantReceiptJson = "{";
        ConsentNotificationEvidence.Matches(notification, proof).Should().BeFalse();
        ConsentNotificationEvidence.Matches(notification, new Transition()).Should().BeFalse();
    }

    private static (Event, Transition) Fixture(ConsentWebhookEventType kind)
    {
        var time = DateTimeOffset.Parse("2026-10-15T10:00:00Z");
        var receipt = new ConsentLifecycleReceipt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ["action:execute"], GrantOrigin.UserExplicit, null, time, time.AddHours(1));
        var frozen = ConsentLifecycleIdentity.Transition(receipt, kind,
            kind == ConsentWebhookEventType.Granted ? time : time.AddHours(1));
        var notification = new Event { Id = frozen.Id, TransitionId = frozen.Id,
            TenantId = receipt.TenantId.ToString("N"), GrantId = receipt.GrantId.ToString("N"),
            UserId = receipt.GrantorUserId.ToString("N"), Scopes = "action:execute",
            EventType = Enum.Parse<Event.WebhookEventType>(kind.ToString()), OccurredAt = frozen.OccurredAt,
            IdempotencyId = frozen.Id, BodyJson = frozen.BodyJson, BodyHash = frozen.BodyHash,
            RegistrationId = Guid.NewGuid().ToString("N"), DestinationRevision = 1,
            DestinationUrl = "https://receiver.example/events" };
        var proof = new Transition { Id = frozen.Id, GrantId = receipt.GrantId.ToString("N"), Kind = kind.ToString(),
            GrantReceiptJson = JsonSerializer.Serialize(receipt), AuthorityJson = "{\"kind\":\"fixture\"}",
            EventId = frozen.Id, BodyHash = frozen.BodyHash, OccurredAt = frozen.OccurredAt,
            RegistrationId = notification.RegistrationId, DestinationRevision = 1, DestinationUrl = notification.DestinationUrl };
        return (notification, proof);
    }
}
