using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

public interface INodeTreasuryStore
{
    /// <summary>Loads one canonical chain/network destination, or null when absent.</summary>
    Task<AZOAResult<NodeTreasuryDestination?>> GetDestinationAsync(
        string chain,
        ChainNetwork network,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically compares the destination version, writes the next value, and
    /// appends its immutable audit snapshot.
    /// </summary>
    /// <param name="destination">Next destination row.</param>
    /// <param name="audit">Audit row for the same version transition.</param>
    /// <param name="expectedVersion">Required version, or null when the row must not exist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A conflict result for a lost race; unexpected failures bubble.</returns>
    Task<AZOAResult<NodeTreasuryDestination>> UpdateDestinationWithAuditAsync(
        NodeTreasuryDestination destination,
        NodeTreasuryAudit audit,
        long? expectedVersion,
        CancellationToken ct = default);

    /// <summary>Loads the newest treasury audit rows up to <paramref name="limit"/>.</summary>
    Task<AZOAResult<IEnumerable<NodeTreasuryAudit>>> ListAuditAsync(
        int limit,
        CancellationToken ct = default);
}
