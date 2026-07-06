// SPDX-License-Identifier: UNLICENSED

using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Backfill;

/// <summary>Context handed to a backfill unit: the SurrealDB executor its rewrite runs through.</summary>
/// <remarks>Rationale + authoring rules: see <c>Services/Backfill/AGENTS.md</c>.</remarks>
public sealed record BackfillContext(ISurrealExecutor Executor);

/// <summary>Outcome of a single <see cref="IBackfill.ApplyAsync"/> call.</summary>
public sealed record BackfillResult(long RowsAffected)
{
    /// <summary>A no-op / nothing-to-do apply (still records the ledger row so it is not retried).</summary>
    public static BackfillResult None { get; } = new(0);
}

/// <summary>
/// A single idempotent data-backfill unit. Registered units are discovered by
/// the <see cref="BackfillRunner"/>, checked against the <c>data_migration</c>
/// ledger, and applied once in <see cref="Order"/> sequence.
/// </summary>
/// <remarks>See <c>Services/Backfill/AGENTS.md</c> for the primitive's contract.</remarks>
public interface IBackfill
{
    /// <summary>Stable, unique identifier; the applied-once dedup key in the ledger. Never reuse or rename.</summary>
    string Id { get; }

    /// <summary>Human-readable name recorded at apply time.</summary>
    string Name { get; }

    /// <summary>Apply sequence; units are applied in ascending order, then by <see cref="Id"/>.</summary>
    int Order { get; }

    /// <summary>Content checksum used to detect a changed body (advisory; the ledger keys on <see cref="Id"/>).</summary>
    string Checksum { get; }

    /// <summary>Idempotently applies the backfill. MUST be safe to re-run: guard on target state, never assume a clean slate.</summary>
    Task<BackfillResult> ApplyAsync(BackfillContext context, CancellationToken ct = default);
}
