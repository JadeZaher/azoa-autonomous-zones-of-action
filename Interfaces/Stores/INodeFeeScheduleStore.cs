using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

public interface INodeFeeScheduleStore
{
    /// <summary>Loads the singleton local fee schedule, or null when unconfigured.</summary>
    Task<AZOAResult<NodeFeeSchedule?>> GetScheduleAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically compares the persisted version, writes the next schedule, and
    /// appends its immutable audit snapshot.
    /// </summary>
    /// <param name="schedule">Next schedule row.</param>
    /// <param name="audit">Audit row for the same version transition.</param>
    /// <param name="expectedVersion">Required version, or null when the row must not exist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A fixed conflict result for a lost race; unexpected failures bubble.</returns>
    Task<AZOAResult<NodeFeeSchedule>> UpdateScheduleWithAuditAsync(
        NodeFeeSchedule schedule,
        NodeFeeAudit audit,
        long? expectedVersion,
        CancellationToken ct = default);

    /// <summary>Loads the newest fee audit rows up to <paramref name="limit"/>.</summary>
    Task<AZOAResult<IEnumerable<NodeFeeAudit>>> ListAuditAsync(int limit, CancellationToken ct = default);
}
