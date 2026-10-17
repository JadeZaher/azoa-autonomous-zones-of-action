namespace AZOA.WebAPI.Models;

/// <summary>Immutable request identity for one explicit consent admission.</summary>
public sealed record ConsentLifecycleCommand(Guid RequestId, Guid TenantId, IReadOnlyList<string> Scopes,
    GrantOrigin Origin, string? ParticipationRef, DateTimeOffset? ExpiresAt);

/// <summary>Frozen original grant result returned on command replay.</summary>
public sealed record ConsentLifecycleReceipt(Guid RequestId, Guid GrantId, Guid GrantorUserId, Guid TenantId,
    IReadOnlyList<string> Scopes, GrantOrigin Origin, string? ParticipationRef,
    DateTimeOffset GrantedAt, DateTimeOffset? ExpiresAt);

/// <summary>Immutable notification bytes for a single persisted consent transition.</summary>
public sealed record ConsentTransitionSnapshot(string Id, string BodyJson, string BodyHash, DateTimeOffset OccurredAt);

/// <summary>Persisted revocation fact, including whether its notification was recorded atomically.</summary>
public sealed record ConsentRevocationReceipt(Guid GrantId, DateTimeOffset RevokedAt, bool NotificationRecorded);

/// <summary>Original atomic membership closure result, preserved across retries.</summary>
public sealed record ConsentOffboardReceipt(Guid TenantId, string ParticipationRef, DateTimeOffset ClosedAt, int RevokedCount);

/// <summary>Proves a command cannot newly admit a grant, or returns its original admission receipt.</summary>
public sealed record ConsentCommandWithdrawal(Guid RequestId, bool Withdrawn, ConsentLifecycleReceipt? Receipt);
