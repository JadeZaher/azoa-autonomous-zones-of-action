using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

public enum NodeTransparencyAuditKind
{
    Governance = 1,
    FeeSchedule = 2,
    Treasury = 3,
}

/// <summary>Internal exclusive keyset position; public callers receive only its protected encoding.</summary>
public sealed record NodeTransparencyStoreCursor(DateTimeOffset OccurredAt, string RecordId);

public interface INodeTransparencyStore
{
    /// <summary>Lists all configured treasury destinations up to the requested bound.</summary>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>At most <paramref name="limit"/> configured destination rows.</returns>
    Task<AZOAResult<IReadOnlyList<NodeTreasuryDestination>>> ListTreasuryDestinationsAsync(
        int limit,
        CancellationToken ct = default);

    /// <summary>Lists governance audit rows before an optional stable composite cursor.</summary>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="before">Exclusive descending <c>(occurred_at,id)</c> cursor, or null for the newest row.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>Rows ordered by occurrence time and record id, both descending.</returns>
    Task<AZOAResult<IReadOnlyList<NodeGovernanceAudit>>> ListGovernanceAuditAsync(
        int limit,
        NodeTransparencyStoreCursor? before,
        CancellationToken ct = default);

    /// <summary>Lists fee-schedule audit rows before an optional stable composite cursor.</summary>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="before">Exclusive descending <c>(occurred_at,id)</c> cursor, or null for the newest row.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>Rows ordered by occurrence time and record id, both descending.</returns>
    Task<AZOAResult<IReadOnlyList<NodeFeeAudit>>> ListFeeAuditAsync(
        int limit,
        NodeTransparencyStoreCursor? before,
        CancellationToken ct = default);

    /// <summary>Lists treasury audit rows before an optional stable composite cursor.</summary>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="before">Exclusive descending <c>(occurred_at,id)</c> cursor, or null for the newest row.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>Rows ordered by occurrence time and record id, both descending.</returns>
    Task<AZOAResult<IReadOnlyList<NodeTreasuryAudit>>> ListTreasuryAuditAsync(
        int limit,
        NodeTransparencyStoreCursor? before,
        CancellationToken ct = default);
}
