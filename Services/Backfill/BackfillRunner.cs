// SPDX-License-Identifier: UNLICENSED

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Services.Backfill;

/// <summary>A backfill's status relative to the data_migration ledger.</summary>
public sealed record BackfillStatus(string Id, string Name, int Order, string Checksum, bool Applied);

/// <summary>Result of applying one backfill unit during a run.</summary>
public sealed record BackfillApplyOutcome(string Id, string Name, bool WasApplied, long RowsAffected, bool AlreadyApplied, string? Error);

/// <summary>
/// Applies pending <see cref="IBackfill"/> units in order and records each in the
/// <c>data_migration</c> ledger, so re-running is a no-op. The two-verb surface
/// is <see cref="ListAsync"/> (status) and <see cref="ApplyPendingAsync"/> (run).
/// </summary>
/// <remarks>
/// Contract + rationale: <c>Services/Backfill/AGENTS.md</c>. The runner is the
/// application-level analogue of surrealforge's schema migration runner but for
/// DATA rewrites; greenfield pre-launch the registry is empty by design.
/// </remarks>
public sealed class BackfillRunner
{
    private readonly IReadOnlyList<IBackfill> _backfills;
    private readonly IDataMigrationLedgerStore _ledger;
    private readonly ISurrealExecutor _executor;
    private readonly ILogger<BackfillRunner> _logger;

    public BackfillRunner(
        IEnumerable<IBackfill> backfills,
        IDataMigrationLedgerStore ledger,
        ISurrealExecutor executor,
        ILogger<BackfillRunner> logger)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _backfills = OrderBackfills(backfills ?? throw new ArgumentNullException(nameof(backfills)));
    }

    /// <summary>Lists every registered backfill with its applied/pending status, in apply order.</summary>
    public async Task<IReadOnlyList<BackfillStatus>> ListAsync(CancellationToken ct = default)
    {
        var applied = await _ledger.GetAppliedIdsAsync(ct);
        return _backfills
            .Select(b => new BackfillStatus(b.Id, b.Name, b.Order, b.Checksum, applied.Contains(b.Id)))
            .ToList();
    }

    /// <summary>
    /// Applies every not-yet-applied backfill in order, recording each on success.
    /// Idempotent: an already-recorded id is skipped; a concurrent runner that
    /// loses the ledger insert race is reported as AlreadyApplied, not an error.
    /// Stops at the first apply failure (ordered rewrites may depend on predecessors).
    /// </summary>
    public async Task<IReadOnlyList<BackfillApplyOutcome>> ApplyPendingAsync(CancellationToken ct = default)
    {
        var applied = new HashSet<string>(await _ledger.GetAppliedIdsAsync(ct), StringComparer.Ordinal);
        var outcomes = new List<BackfillApplyOutcome>();
        var context = new BackfillContext(_executor);

        foreach (var backfill in _backfills)
        {
            ct.ThrowIfCancellationRequested();

            if (applied.Contains(backfill.Id))
            {
                outcomes.Add(new BackfillApplyOutcome(backfill.Id, backfill.Name, false, 0, true, null));
                continue;
            }

            try
            {
                var result = await backfill.ApplyAsync(context, ct);
                var record = new AppliedBackfillRecord(
                    backfill.Id, backfill.Name, backfill.Checksum, result.RowsAffected, DateTimeOffset.UtcNow);

                var recorded = await _ledger.TryRecordAppliedAsync(record, ct);
                if (recorded)
                {
                    _logger.LogInformation(
                        "Backfill {BackfillId} ({Name}) applied: {Rows} row(s).",
                        backfill.Id, backfill.Name, result.RowsAffected);
                    outcomes.Add(new BackfillApplyOutcome(backfill.Id, backfill.Name, true, result.RowsAffected, false, null));
                }
                else
                {
                    // A concurrent runner recorded it first. The apply above is
                    // idempotent by contract, so this is a benign no-op, not a fault.
                    outcomes.Add(new BackfillApplyOutcome(backfill.Id, backfill.Name, false, 0, true, null));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Backfill {BackfillId} ({Name}) failed; halting the run.",
                    backfill.Id, backfill.Name);
                outcomes.Add(new BackfillApplyOutcome(backfill.Id, backfill.Name, false, 0, false, ex.Message));
                break; // ordered rewrites: do not run successors past a failure
            }
        }

        return outcomes;
    }

    private static IReadOnlyList<IBackfill> OrderBackfills(IEnumerable<IBackfill> backfills)
    {
        var list = backfills.ToList();

        // Duplicate ids collapse the applied-once dedup key -> reject at composition time.
        var dupe = list.GroupBy(b => b.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dupe is not null)
            throw new InvalidOperationException($"Duplicate IBackfill.Id '{dupe.Key}' registered — ids must be unique.");

        return list
            .OrderBy(b => b.Order)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();
    }
}
