using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

public interface INodeGovernanceStore
{
    /// <summary>Loads the singleton local governance policy, or null when unconfigured.</summary>
    Task<AZOAResult<NodeGovernanceParameters?>> GetParametersAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically compares the persisted version, replaces the singleton policy,
    /// and appends its immutable audit row in one transaction.
    /// </summary>
    /// <param name="parameters">Next policy row.</param>
    /// <param name="audit">Audit row describing the same version transition.</param>
    /// <param name="expectedVersion">
    /// Persisted version required for update, or null only when the row must not
    /// exist yet.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A version-conflict error result for a lost race; unexpected failures bubble.</returns>
    Task<AZOAResult<NodeGovernanceParameters>> UpdateParametersWithAuditAsync(
        NodeGovernanceParameters parameters,
        NodeGovernanceAudit audit,
        long? expectedVersion,
        CancellationToken ct = default);

    /// <summary>Lists newest governance audit rows up to the supplied bound.</summary>
    Task<AZOAResult<IEnumerable<NodeGovernanceAudit>>> ListAuditAsync(int limit, CancellationToken ct = default);
}
