using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyb;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Business verification for the tenant identified by authenticated claims.</summary>
public interface IKybService
{
    /// <summary>Reports the operator-configured business verification availability.</summary>
    HostedSessionCapabilities GetCapabilities();
    /// <summary>Ensures the current tenant attempt and persists admission before creating a hosted redirect.</summary>
    Task<AZOAResult<KybSessionResponse>> BeginAsync(Guid tenantId, string requestKey, CancellationToken ct = default);
    /// <summary>Returns current tenant business verification without provider ids or personal attributes.</summary>
    Task<AZOAResult<KybStatusResponse?>> GetStatusAsync(Guid tenantId, CancellationToken ct = default);
    /// <summary>Re-fetches the current tenant session from Identomat when a callback was delayed.</summary>
    Task<AZOAResult<KybStatusResponse?>> RefreshAsync(Guid tenantId, CancellationToken ct = default);
    /// <summary>Authenticates the callback and processes only an authoritative server-fetched outcome.</summary>
    Task HandleCallbackAsync(string rawBody, CancellationToken ct = default);
}
