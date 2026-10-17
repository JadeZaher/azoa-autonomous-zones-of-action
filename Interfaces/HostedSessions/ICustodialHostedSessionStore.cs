using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.HostedSessions;

/// <summary>Authorizes one hosted KYC create under current tenant custody authority.</summary>
public interface ICustodialHostedSessionStore
{
    /// <summary>Atomically claims first egress with the exact subject, tenant and current enrollment.</summary>
    Task<AZOAResult<HostedSession>> TryBeginCustodialCreationAsync(Guid id, string creationToken,
        DateTimeOffset now, TenantCustodialWitness witness, CancellationToken ct = default);
}
