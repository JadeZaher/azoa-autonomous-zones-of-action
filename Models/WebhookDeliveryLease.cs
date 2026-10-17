using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Models;

/// <summary>One authorized delivery attempt; never exposed by event discovery endpoints.</summary>
public sealed record WebhookDeliveryLease(string EventId, long Version, string LeaseId, DateTimeOffset ExpiresAt,
    Guid TenantId, Guid RegistrationId, long DestinationRevision, string Url, string BodyJson,
    string IdempotencyId, long AttemptCount, long SecretRevision,
    [property: JsonIgnore] string EncryptedSecret, long CycleAttemptCount = 1);

public enum WebhookAttemptOutcome { Delivered, Retry, DeadLetter }
