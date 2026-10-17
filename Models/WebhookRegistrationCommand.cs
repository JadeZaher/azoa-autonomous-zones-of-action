namespace AZOA.WebAPI.Models;

public sealed record WebhookRegistrationCommand(Guid RequestId, Guid RegistrationId, long ExpectedVersion,
    string Url, bool IsActive, bool RotateSecret, string Reason, bool ReplaceLegacy = false, string? LegacyFingerprint = null);

public sealed record WebhookRegistrationReceipt(Guid RequestId, Guid RegistrationId, Guid TenantId,
    long Version, long DestinationRevision, long SecretRevision, string Url, bool IsActive, DateTimeOffset RecordedAt);

public sealed record WebhookRegistrationMutationResult(WebhookRegistrationReceipt Receipt, string? NewSecret, bool Replayed);

public sealed record WebhookRegistrationStatus(Guid RegistrationId, Guid TenantId, long Version,
    long DestinationRevision, long SecretRevision, string Url, bool IsActive, DateTime CreatedAt,
    DateTime? SecretRotatedAt, string? LegacyFingerprint = null);

public sealed record WebhookRegistrationWithdrawal(Guid RequestId, bool AlreadyApplied, WebhookRegistrationReceipt? Receipt);
