namespace AZOA.WebAPI.Models;

public sealed record WebhookEventRetryCommand(Guid RequestId, long ExpectedVersion, string Reason);

/// <summary><paramref name="RetriedFrom"/> names the undelivered state the cycle restarted from (null on pre-round-4 receipts).</summary>
public sealed record WebhookEventRetryReceipt(Guid RequestId, Guid EventId, Guid TenantId, long Version,
    long RetryGeneration, DateTimeOffset RecordedAt, string? RetriedFrom = null);

/// <summary>Delivery metadata deliberately excludes event payload and protected attempt material.</summary>
public sealed record WebhookEventStatus(Guid EventId, string EventType, Guid RunId, Guid NodeId, string Status,
    long Version, long AttemptCount, long CycleAttemptCount, long RetryGeneration, DateTimeOffset OccurredAt,
    DateTimeOffset NextAttemptAt, string? ErrorCode, Guid? RegistrationId, long? DestinationRevision, bool AttemptInFlight,
    bool Retryable);

/// <summary>Consent delivery metadata without scopes, payload or protected attempt material.</summary>
public sealed record ConsentWebhookEventStatus(Guid EventId, string EventType, Guid GrantId, Guid UserId, string Status,
    long Version, long AttemptCount, long CycleAttemptCount, long RetryGeneration, DateTimeOffset OccurredAt,
    DateTimeOffset NextAttemptAt, string? ErrorCode, Guid? RegistrationId, long? DestinationRevision, bool AttemptInFlight,
    bool Retryable);
