using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Append-only persistence for <see cref="ConsentAuditEntry"/> rows (AC10/L1).
/// Write-only by contract: there is no update or delete surface — an audit trail is
/// immutable. Independent of the (best-effort) webhook emitter.
/// </summary>
public interface IConsentAuditStore
{
    /// <summary>Append one immutable audit row. Best-effort durability: a write
    /// failure must NOT mask the underlying decision, but is surfaced as an error
    /// result for logging.</summary>
    Task<AZOAResult<bool>> AppendAsync(ConsentAuditEntry entry, CancellationToken ct = default);

    /// <summary>Tenant-scoped audit read (operator/tenant visibility). Only this
    /// tenant's rows.</summary>
    Task<AZOAResult<IEnumerable<ConsentAuditEntry>>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);
}
