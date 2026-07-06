// SPDX-License-Identifier: UNLICENSED

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IDataMigrationLedgerStore"/>. The backfill id is
/// the record id AND a UNIQUE-indexed column, so a duplicate CREATE is rejected
/// per-statement — that rejection is the canonical "already applied" signal
/// (mirrors <see cref="SurrealBridgeStore.TryInsertConsumedVaaAsync"/>).
/// </summary>
/// <remarks>See <c>Services/Backfill/AGENTS.md</c>.</remarks>
public sealed class SurrealDataMigrationLedgerStore : IDataMigrationLedgerStore
{
    private const string Table = "data_migration";

    private readonly ISurrealExecutor _executor;

    public SurrealDataMigrationLedgerStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<IReadOnlyCollection<string>> GetAppliedIdsAsync(CancellationToken ct = default)
    {
        var q = SurrealQuery.Of("SELECT backfill_id FROM data_migration");
        var rows = await _executor.QueryAsync<DataMigration>(q, ct);
        return rows
            .Select(r => r.BackfillId)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<AppliedBackfillRecord>> ListAppliedAsync(CancellationToken ct = default)
    {
        var q = SurrealQuery.Of("SELECT * FROM data_migration ORDER BY applied_at DESC");
        var rows = await _executor.QueryAsync<DataMigration>(q, ct);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<bool> TryRecordAppliedAsync(AppliedBackfillRecord record, CancellationToken ct = default)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        if (string.IsNullOrWhiteSpace(record.BackfillId))
            throw new ArgumentException("BackfillId must not be empty.", nameof(record));

        var poco = new DataMigration
        {
            Id           = record.BackfillId,
            BackfillId   = record.BackfillId,
            Name         = record.Name,
            Checksum     = record.Checksum,
            RowsAffected = record.RowsAffected,
            AppliedAt    = record.AppliedAt,
        };

        // CREATE by deterministic id; a UNIQUE collision (id OR backfill_id)
        // surfaces per-statement as ERR (or a transport-level exception) and is
        // the "already applied" signal -> return false. Inspect status PER
        // statement to avoid swallowing a genuine multi-statement failure.
        var q = SurrealQuery
            .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
            .WithParam("_t",   Table)
            .WithParam("_id",  record.BackfillId)
            .WithParam("_body", poco);

        SurrealResponse response;
        try
        {
            response = await _executor.ExecuteAsync(q, ct);
        }
        catch (SurrealStatementException)
        {
            return false; // UNIQUE violation == already applied
        }

        if (response.Count == 0) return false;
        var stmt = response[0];
        if (!stmt.IsOk) return false;
        return stmt.AffectedCount() == 1;
    }

    private static AppliedBackfillRecord ToRecord(DataMigration p) => new(
        BackfillId:   p.BackfillId,
        Name:         p.Name,
        Checksum:     p.Checksum,
        RowsAffected: p.RowsAffected,
        AppliedAt:    p.AppliedAt);
}
