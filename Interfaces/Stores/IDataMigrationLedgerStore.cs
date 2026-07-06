// SPDX-License-Identifier: UNLICENSED

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>A recorded application of a backfill unit (one row of the data_migration ledger).</summary>
public sealed record AppliedBackfillRecord(
    string BackfillId,
    string Name,
    string? Checksum,
    long RowsAffected,
    System.DateTimeOffset AppliedAt);

/// <summary>
/// Persistence boundary for the <c>data_migration</c> backfill ledger. Records
/// which <c>IBackfill</c> units have been applied so the runner can skip them.
/// Distinct from surrealforge's <c>schema_migration</c> DDL ledger.
/// </summary>
/// <remarks>See <c>Services/Backfill/AGENTS.md</c>.</remarks>
public interface IDataMigrationLedgerStore
{
    /// <summary>The set of backfill ids already recorded applied.</summary>
    Task<IReadOnlyCollection<string>> GetAppliedIdsAsync(CancellationToken ct = default);

    /// <summary>All applied-backfill records, most recent first.</summary>
    Task<IReadOnlyList<AppliedBackfillRecord>> ListAppliedAsync(CancellationToken ct = default);

    /// <summary>
    /// Records a backfill as applied. Returns false iff the backfill id was
    /// already present (UNIQUE rejected the insert) — the canonical
    /// already-applied signal; true means this call recorded it.
    /// </summary>
    Task<bool> TryRecordAppliedAsync(AppliedBackfillRecord record, CancellationToken ct = default);
}
