using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Current tenant business verification with single-winner attempt and outcome transitions.</summary>
public interface IKybVerificationStore
{
    /// <summary>Reads the current verification for a claims-derived tenant.</summary>
    Task<KybVerification?> GetAsync(Guid tenantId, CancellationToken ct = default);
    /// <summary>Creates or replaces exactly the expected version; null means a concurrent caller won.</summary>
    Task<KybVerification?> TryStartAsync(KybVerification? expected, KybVerification candidate, CancellationToken ct = default);
    /// <summary>Attaches the hosted session only to the same current attempt.</summary>
    Task<bool> AttachSessionAsync(Guid tenantId, string attemptId, Guid sessionId, CancellationToken ct = default);
    /// <summary>Decides an unexpired pending attempt only under its captured authority and version.</summary>
    Task<KybVerification?> TryDecideAsync(KybVerification expected, HostedSession session, string status, string? evidence,
        DateTimeOffset reviewedAt, DateTimeOffset expiresAt, CancellationToken ct = default);
}
