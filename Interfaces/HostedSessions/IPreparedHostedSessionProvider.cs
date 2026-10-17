using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.HostedSessions;

/// <summary>Persists immutable provider requests before admission and supports authenticated recovery.</summary>
public interface IPreparedHostedSessionProvider : IHostedSessionProvider
{
    Task<AZOAResult<bool>> PrepareCreationAsync(HostedSessionRequest request, CancellationToken ct);
    Task<AZOAResult<HostedSessionCreation>> RecoverCreationAsync(HostedSession session, CancellationToken ct);
    AZOAResult<HostedCallbackTrigger> VerifyCallback(string rawBody, IReadOnlyDictionary<string, string> headers);
}
