// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// NODE-OPERATOR-ONLY persistence for AML screening results, monitoring
/// registrations, and the single cold-poll high-water mark.
///
/// <para>Every read that can reach a caller is scoped by tenant. The AML surface
/// keys on a person's NAME and DATE OF BIRTH, which are shared across tenants by
/// nature, so an unscoped predicate here resolves one tenant's query to another
/// tenant's row — see <c>Persistence/SurrealDb/Models/KycScreeningMonitor.cs</c>.</para>
/// </summary>
public interface IKycScreeningStore
{
    Task<AZOAResult<KycScreeningResult>> SaveResultAsync(
        KycScreeningResult result,
        CancellationToken ct = default);

    /// <summary>
    /// The single row a value gate reads: the most recent screening for this exact
    /// (user, tenant) pair. A null result means NOT SCREENED, which the gate must
    /// treat as a denial, never as an absence of adverse findings.
    /// </summary>
    Task<AZOAResult<KycScreeningResult>> GetLatestResultAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken ct = default);

    Task<AZOAResult<IReadOnlyList<KycScreeningResult>>> GetResultsForUserAsync(
        Guid userId,
        Guid? tenantId,
        int limit,
        CancellationToken ct = default);

    Task<AZOAResult<KycScreeningMonitor>> SaveMonitorAsync(
        KycScreeningMonitor monitor,
        CancellationToken ct = default);

    /// <summary>
    /// Looks a monitor up by the exact registered query shape WITHIN one uniqueness
    /// scope. The scope is not optional: the query shape alone is a person's name and
    /// date of birth, which another tenant may legitimately be monitoring.
    /// </summary>
    Task<AZOAResult<KycScreeningMonitor>> GetMonitorByQueryAsync(
        string tenantScope,
        string? queryFullName,
        string? queryBirthdayTimeFrom,
        CancellationToken ct = default);

    /// <summary>
    /// Node-internal reconciliation for the vendor's <c>already-exists</c> reply.
    /// Returns ONLY the opaque provider monitor id — never a row, so it is
    /// structurally incapable of handing one tenant another tenant's subject user
    /// or tenant id. One vendor company key serves the whole node, so the vendor
    /// dedups a query shape globally and this is the only way a second tenant can
    /// learn the id of the upstream monitor it must share.
    /// </summary>
    Task<AZOAResult<string>> GetProviderMonitorIdByQueryAsync(
        string? queryFullName,
        string? queryBirthdayTimeFrom,
        CancellationToken ct = default);

    /// <summary>
    /// Every local row backing one upstream monitor — one per tenant that registered
    /// the same query shape. The poll fans a SINGLE follow-up search out across them
    /// rather than making one vendor call per tenant.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<KycScreeningMonitor>>> GetMonitorsByProviderIdAsync(
        string providerMonitorId,
        CancellationToken ct = default);

    /// <summary>Marks one tenant's row removed; other tenants keep monitoring.</summary>
    Task<AZOAResult<bool>> MarkMonitorRemovedAsync(
        string tenantScope,
        string providerMonitorId,
        DateTimeOffset removedAt,
        CancellationToken ct = default);

    /// <summary>Stamps a poll pass over a monitor that did not change.</summary>
    Task<AZOAResult<bool>> TouchMonitorPolledAsync(
        string monitorId,
        DateTimeOffset polledAt,
        CancellationToken ct = default);

    /// <summary>Advances a monitor's observed high-water mark after a follow-up fetch.</summary>
    Task<AZOAResult<bool>> RecordMonitorFollowUpAsync(
        string monitorId,
        string? observedUpdateDate,
        DateTimeOffset followedUpAt,
        CancellationToken ct = default);

    /// <summary>
    /// Tombstones the retained query shape (name + date of birth) on every screening
    /// result and monitor belonging to these users inside one tenant scope, while
    /// preserving the verdict, the provider ids, and the audit timestamps (D25).
    /// </summary>
    Task<AZOAResult<int>> EraseSubjectAsync(
        IReadOnlyList<string> userIds,
        Guid? tenantId,
        string reason,
        DateTimeOffset erasedAt,
        CancellationToken ct = default);

    /// <summary>Tombstones every screening row whose own retention window elapsed.</summary>
    Task<AZOAResult<int>> EraseRetentionExpiredAsync(
        DateTimeOffset asOf,
        string reason,
        int limit,
        CancellationToken ct = default);

    Task<AZOAResult<KycScreeningPollState>> GetPollStateAsync(CancellationToken ct = default);

    Task<AZOAResult<bool>> RecordPollSuccessAsync(
        string? highWaterMarkDate,
        DateTimeOffset completedAt,
        int changedCount,
        CancellationToken ct = default);

    /// <summary>Records a failed cycle and parks polling with bounded backoff.</summary>
    Task<AZOAResult<bool>> RecordPollFailureAsync(
        DateTimeOffset failedAt,
        CancellationToken ct = default);
}
