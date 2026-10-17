using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.HostedSessions;

/// <summary>Stores immutable hosted request bytes, recovery leases and authoritative evidence identity.</summary>
public interface IHostedProviderAttemptStore
{
    Task<HostedProviderAttempt> EnsureAsync(HostedProviderAttempt candidate, CancellationToken ct);
    Task<HostedProviderAttempt?> GetAsync(string id, CancellationToken ct);
    Task<HostedProviderAttempt?> GetByRemoteAsync(string adapter, string origin, string sessionId, CancellationToken ct);
    Task<IReadOnlyList<HostedProviderAttempt>> ListDueAsync(DateTimeOffset now, CancellationToken ct);
    Task ScheduleAsync(string id, DateTimeOffset nextAttemptAt, bool completed, CancellationToken ct);
    Task<HostedProviderAttempt?> ClaimAsync(HostedSession session, bool allowReplay, DateTimeOffset now, CancellationToken ct);
    Task<HostedProviderAttempt?> SaveCreationAsync(HostedProviderAttempt lease, HostedSessionCreation creation, CancellationToken ct);
    Task<bool> PinTerminalAsync(string hostedSessionId, string remoteSessionId, string decisionId, string digest, CancellationToken ct);
    Task<HostedProviderCallback?> AdmitCallbackAsync(HostedProviderCallback candidate, CancellationToken ct);
    Task CompleteCallbackAsync(string id, CancellationToken ct);
}
