using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Signing;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionWebhookEvent;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Serializes event attempts with registration management and fences stale delivery holders.</summary>
public interface IActionWebhookDeliveryStore
{
    /// <summary>Lists tenant-scoped delivery metadata without payloads or signing material.</summary>
    Task<IReadOnlyList<WebhookEventStatus>> ListAsync(Guid tenantId, int offset, int limit, CancellationToken ct = default);

    /// <summary>Reads one tenant-scoped delivery status without exposing protected material.</summary>
    Task<WebhookEventStatus?> GetAsync(Guid tenantId, Guid eventId, CancellationToken ct = default);

    /// <summary>Audits a fresh retry cycle against exact dead-letter version and unchanged original destination.</summary>
    Task<WebhookEventRetryReceipt?> RetryAsync(Guid tenantId, Guid eventId, WebhookEventRetryCommand command,
        SigningConsentWitness authority, CancellationToken ct = default);
    /// <summary>Returns bounded due candidates; discovery does not authorize any send.</summary>
    Task<IReadOnlyList<Event>> ListCandidatesAsync(int limit, CancellationToken ct = default);

    /// <summary>Claims the exact event epoch and current pinned registration/secret once for a bounded lease.</summary>
    Task<WebhookDeliveryLease?> TryClaimAsync(string eventId, long expectedVersion, TimeSpan duration,
        int maximumAttempts, CancellationToken ct = default);

    /// <summary>Completes only the unexpired exact holder and removes retained signing material.</summary>
    Task<bool> FinishAsync(WebhookDeliveryLease lease, WebhookAttemptOutcome outcome, TimeSpan retryDelay,
        string? errorCode, CancellationToken ct = default);

    /// <summary>Dead-letters an unleased exhausted or unavailable event without changing its original destination.</summary>
    Task<bool> SuspendUnavailableAsync(string eventId, long expectedVersion, int maximumAttempts, CancellationToken ct = default);
}
