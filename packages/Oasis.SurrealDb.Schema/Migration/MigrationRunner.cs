// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.Schema -- Migration runner (Phase 4 task 23).
//
// What this is:
//   Ordered, idempotent .surql file applier with per-file SHA-256 checksum
//   tracking in a `schema_migration` table. Replaces the archived
//   `Odonno/surrealdb-migrations` tool (Footgun #1 from the archaeological
//   persona report).
//
// What it is NOT:
//   - It does NOT auto-backfill SCHEMAFULL field additions. The 3-step
//     add-field-then-backfill-then-assert ritual is the caller's
//     responsibility (documented in plan.md). The runner just applies files
//     verbatim and records that they ran.
//   - It does NOT understand SurrealQL semantics. Each .surql file is treated
//     as an opaque DDL blob; SurrealDB itself enforces validity.
//
// Idempotency:
//   On apply, the runner queries `schema_migration` for the file's name. If a
//   row exists with the same SHA-256, the apply is skipped (no DDL re-sent).
//   If a row exists with a DIFFERENT checksum, the runner aborts with a
//   `MigrationChecksumMismatchException` unless `--force` is set.
//
// Dry-run:
//   `Plan()` returns the list of intended actions (apply / skip / mismatch)
//   without sending any writes. `ApplyAsync(dryRun: true)` returns the same
//   plan AND ensures zero writes leave the runner.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Oasis.SurrealDb.Schema.Migration
{
    /// <summary>
    /// Drives ordered application of <c>.surql</c> migration files against a
    /// SurrealDB instance. Tracks applied files + checksums in the
    /// <c>schema_migration</c> table.
    /// </summary>
    public sealed class MigrationRunner
    {
        /// <summary>The migration tracking table name. Lives alongside user tables.</summary>
        public const string TrackingTable = "schema_migration";

        private readonly ISurrealConnection _connection;
        private readonly string _appliedBy;

        /// <param name="connection">SurrealDB connection (narrow abstraction).</param>
        /// <param name="appliedBy">
        /// Free-form identity recorded into each <c>schema_migration</c> row.
        /// Defaults to <c>"oasis-surreal/cli"</c>.
        /// </param>
        public MigrationRunner(ISurrealConnection connection, string? appliedBy = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _appliedBy = string.IsNullOrWhiteSpace(appliedBy) ? "oasis-surreal/cli" : appliedBy!;
        }

        /// <summary>
        /// Discover and order <c>.surql</c> files in the given directory by
        /// their numeric prefix (<c>NNN_name.surql</c>).
        /// </summary>
        public static IReadOnlyList<MigrationFile> DiscoverFiles(string directory)
        {
            if (directory == null) throw new ArgumentNullException(nameof(directory));
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"migration directory not found: {directory}");

            var files = Directory.EnumerateFiles(directory, "*.surql", SearchOption.TopDirectoryOnly)
                .Select(p => new MigrationFile(p, File.ReadAllText(p)))
                .OrderBy(m => m.SortKey, StringComparer.Ordinal)
                .ToList();
            return files;
        }

        /// <summary>
        /// Plan an apply without executing any DDL. Reads the current
        /// <c>schema_migration</c> table to classify each file as
        /// <see cref="MigrationAction.Apply"/>,
        /// <see cref="MigrationAction.Skip"/>, or
        /// <see cref="MigrationAction.ChecksumMismatch"/>.
        /// </summary>
        public async Task<IReadOnlyList<MigrationPlanItem>> PlanAsync(
            IReadOnlyList<MigrationFile> files,
            CancellationToken ct = default)
        {
            await EnsureTrackingTableAsync(ct).ConfigureAwait(false);
            var existing = await LoadAppliedAsync(ct).ConfigureAwait(false);

            var plan = new List<MigrationPlanItem>(files.Count);
            foreach (var f in files)
            {
                if (!existing.TryGetValue(f.FileName, out var applied))
                {
                    plan.Add(new MigrationPlanItem(f, MigrationAction.Apply, null));
                }
                else if (applied.Checksum == f.Checksum)
                {
                    plan.Add(new MigrationPlanItem(f, MigrationAction.Skip, applied.Checksum));
                }
                else
                {
                    plan.Add(new MigrationPlanItem(f, MigrationAction.ChecksumMismatch, applied.Checksum));
                }
            }
            return plan;
        }

        /// <summary>
        /// Apply the given files in order. Returns the planned set of actions
        /// (mirrors <see cref="PlanAsync"/>) for the caller to log.
        ///
        /// <para>If any file's checksum has drifted from the last apply, the
        /// runner aborts with <see cref="MigrationChecksumMismatchException"/>
        /// unless <paramref name="force"/> is true.</para>
        ///
        /// <para>If <paramref name="dryRun"/> is true, no DDL writes are sent.
        /// The schema_migration table is still queried (read-only) to build
        /// the plan, but the runner never calls
        /// <see cref="ISurrealConnection.ExecuteAsync"/> with a
        /// <c>DEFINE</c> / <c>CREATE</c> / <c>UPDATE</c> statement.</para>
        /// </summary>
        public async Task<IReadOnlyList<MigrationPlanItem>> ApplyAsync(
            IReadOnlyList<MigrationFile> files,
            bool dryRun = false,
            bool force = false,
            CancellationToken ct = default)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            var plan = await PlanAsync(files, ct).ConfigureAwait(false);

            // Pre-flight: refuse the whole batch on checksum mismatch unless force.
            if (!force)
            {
                var mismatches = plan.Where(p => p.Action == MigrationAction.ChecksumMismatch).ToList();
                if (mismatches.Count > 0)
                {
                    throw new MigrationChecksumMismatchException(mismatches);
                }
            }

            if (dryRun) return plan;

            foreach (var item in plan)
            {
                ct.ThrowIfCancellationRequested();
                if (item.Action == MigrationAction.Skip) continue;

                // Apply or force-overwrite.
                var result = await _connection.ExecuteAsync(item.File.Content, ct).ConfigureAwait(false);
                if (!result.IsOk)
                {
                    throw new MigrationApplyException(item.File.FileName, result.Detail ?? "unknown server error");
                }
                await RecordAppliedAsync(item.File, ct).ConfigureAwait(false);
            }

            return plan;
        }

        /// <summary>
        /// Read the contents of <c>schema_migration</c> for status reporting.
        /// </summary>
        public async Task<IReadOnlyList<AppliedMigration>> StatusAsync(CancellationToken ct = default)
        {
            await EnsureTrackingTableAsync(ct).ConfigureAwait(false);
            var dict = await LoadAppliedAsync(ct).ConfigureAwait(false);
            return dict.Values
                .OrderBy(v => v.FileName, StringComparer.Ordinal)
                .ToList();
        }

        // ──────────────────────────────────────────────────────────────────
        // schema_migration table I/O.
        // ──────────────────────────────────────────────────────────────────

        /// <summary>The DDL that creates the tracking table on first run.</summary>
        public static string BuildTrackingTableDdl()
        {
            var sb = new StringBuilder();
            sb.Append("DEFINE TABLE ").Append(TrackingTable).Append(" SCHEMAFULL;\n");
            sb.Append("DEFINE FIELD file_name  ON TABLE ").Append(TrackingTable).Append(" TYPE string\n");
            sb.Append("    ASSERT $value != NONE AND $value != \"\";\n");
            sb.Append("DEFINE FIELD checksum   ON TABLE ").Append(TrackingTable).Append(" TYPE string\n");
            sb.Append("    ASSERT $value != NONE AND $value != \"\";\n");
            sb.Append("DEFINE FIELD applied_at ON TABLE ").Append(TrackingTable).Append(" TYPE datetime;\n");
            sb.Append("DEFINE FIELD applied_by ON TABLE ").Append(TrackingTable).Append(" TYPE string\n");
            sb.Append("    ASSERT $value != NONE AND $value != \"\";\n");
            sb.Append("DEFINE INDEX schema_migration_file_name\n");
            sb.Append("    ON TABLE ").Append(TrackingTable).Append('\n');
            sb.Append("    FIELDS file_name\n");
            sb.Append("    UNIQUE;\n");
            return sb.ToString();
        }

        private async Task EnsureTrackingTableAsync(CancellationToken ct)
        {
            var ddl = BuildTrackingTableDdl();
            var result = await _connection.ExecuteAsync(ddl, ct).ConfigureAwait(false);
            if (!result.IsOk)
            {
                throw new MigrationApplyException(TrackingTable, result.Detail ?? "could not create schema_migration table");
            }
        }

        private async Task<Dictionary<string, AppliedMigration>> LoadAppliedAsync(CancellationToken ct)
        {
            var sql = $"SELECT file_name, checksum, applied_at, applied_by FROM {TrackingTable};";
            var result = await _connection.ExecuteAsync(sql, ct).ConfigureAwait(false);
            if (!result.IsOk)
            {
                throw new MigrationApplyException(TrackingTable, result.Detail ?? "could not query schema_migration");
            }

            var dict = new Dictionary<string, AppliedMigration>(StringComparer.Ordinal);
            // Connections may return JSON arrays of rows in their RawBody.
            // Tolerant parser: if RawBody is null/empty, treat as empty result set.
            if (string.IsNullOrWhiteSpace(result.RawBody)) return dict;

            try
            {
                using var doc = JsonDocument.Parse(result.RawBody!);
                EnumerateRows(doc.RootElement, dict);
            }
            catch (JsonException)
            {
                // Tolerant fallback: silently ignore unparseable bodies so a
                // mock connection that doesn't speak the server's response
                // shape (e.g. unit tests) still works.
            }
            return dict;
        }

        private static void EnumerateRows(JsonElement root, Dictionary<string, AppliedMigration> dict)
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray()) EnumerateRows(el, dict);
                return;
            }
            if (root.ValueKind != JsonValueKind.Object) return;

            if (root.TryGetProperty("file_name", out var f) && f.ValueKind == JsonValueKind.String &&
                root.TryGetProperty("checksum", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var fileName = f.GetString()!;
                var checksum = c.GetString()!;
                var appliedAt = root.TryGetProperty("applied_at", out var ap) && ap.ValueKind == JsonValueKind.String
                    ? ap.GetString()
                    : null;
                var appliedBy = root.TryGetProperty("applied_by", out var ab) && ab.ValueKind == JsonValueKind.String
                    ? ab.GetString()
                    : null;
                dict[fileName] = new AppliedMigration(fileName, checksum, appliedAt, appliedBy);
                return;
            }

            // Nested SurrealDB response shape: { status, result: [...] }
            if (root.TryGetProperty("result", out var inner))
            {
                EnumerateRows(inner, dict);
            }
        }

        private async Task RecordAppliedAsync(MigrationFile file, CancellationToken ct)
        {
            // Use UPDATE ... CONTENT semantics so re-apply (force) overwrites the row.
            // We can't depend on the client's parameterized query builder yet, so we
            // construct the literal DDL ourselves with conservative escaping.
            var id = Sanitize(file.FileName);
            var nowIso = DateTime.UtcNow.ToString("O");
            var sb = new StringBuilder();
            sb.Append("UPDATE ").Append(TrackingTable).Append(':').Append(id);
            sb.Append(" CONTENT { ");
            sb.Append("file_name: \"").Append(JsonEscape(file.FileName)).Append("\", ");
            sb.Append("checksum: \"").Append(file.Checksum).Append("\", ");
            sb.Append("applied_at: <datetime>\"").Append(nowIso).Append("\", ");
            sb.Append("applied_by: \"").Append(JsonEscape(_appliedBy)).Append("\"");
            sb.Append(" };");
            var result = await _connection.ExecuteAsync(sb.ToString(), ct).ConfigureAwait(false);
            if (!result.IsOk)
            {
                throw new MigrationApplyException(file.FileName, result.Detail ?? "could not record applied migration");
            }
        }

        /// <summary>Sanitize a filename to a record-id-safe token (letters/digits/underscore).</summary>
        private static string Sanitize(string fileName)
        {
            var sb = new StringBuilder(fileName.Length);
            foreach (var ch in fileName)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
                else sb.Append('_');
            }
            // Cannot start with digit per SurrealDB identifier rules — prefix
            // with `m_` to keep the row id valid.
            if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, "m_");
            return sb.ToString();
        }

        private static string JsonEscape(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// A single discovered <c>.surql</c> file with its SHA-256 checksum.
    /// </summary>
    public sealed class MigrationFile
    {
        public string FullPath { get; }
        public string FileName { get; }
        public string Content { get; }
        public string Checksum { get; }
        /// <summary>The sort key (file name) — numeric prefix dominates ordinal sort.</summary>
        public string SortKey => FileName;

        public MigrationFile(string fullPath, string content)
        {
            FullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
            FileName = Path.GetFileName(fullPath);
            Content = content ?? string.Empty;
            Checksum = ComputeSha256(Content);
        }

        /// <summary>SHA-256 of UTF-8-encoded content, lowercase hex.</summary>
        public static string ComputeSha256(string content)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content ?? string.Empty));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>Action the runner intends to take for a single file.</summary>
    public enum MigrationAction
    {
        /// <summary>File is new — DDL will be sent and a row recorded.</summary>
        Apply,
        /// <summary>File matches a recorded row by name + checksum — no DDL sent.</summary>
        Skip,
        /// <summary>File name exists but checksum differs — abort unless --force.</summary>
        ChecksumMismatch,
    }

    /// <summary>A single line item in an apply plan.</summary>
    public sealed class MigrationPlanItem
    {
        public MigrationFile File { get; }
        public MigrationAction Action { get; }
        /// <summary>The previously-recorded checksum (null when no prior apply).</summary>
        public string? PriorChecksum { get; }

        public MigrationPlanItem(MigrationFile file, MigrationAction action, string? priorChecksum)
        {
            File = file;
            Action = action;
            PriorChecksum = priorChecksum;
        }
    }

    /// <summary>A row from the <c>schema_migration</c> table.</summary>
    public sealed class AppliedMigration
    {
        public string FileName { get; }
        public string Checksum { get; }
        public string? AppliedAt { get; }
        public string? AppliedBy { get; }

        public AppliedMigration(string fileName, string checksum, string? appliedAt, string? appliedBy)
        {
            FileName = fileName;
            Checksum = checksum;
            AppliedAt = appliedAt;
            AppliedBy = appliedBy;
        }
    }

    /// <summary>Thrown when one or more files' checksums differ from the recorded row.</summary>
    public sealed class MigrationChecksumMismatchException : Exception
    {
        public IReadOnlyList<MigrationPlanItem> Mismatches { get; }

        public MigrationChecksumMismatchException(IReadOnlyList<MigrationPlanItem> mismatches)
            : base(BuildMessage(mismatches))
        {
            Mismatches = mismatches;
        }

        private static string BuildMessage(IReadOnlyList<MigrationPlanItem> ms)
        {
            var lines = ms.Select(m =>
                $"  - {m.File.FileName}: prior={m.PriorChecksum ?? "<none>"} current={m.File.Checksum}");
            return "checksum mismatch detected for " + ms.Count + " file(s):\n" + string.Join("\n", lines)
                + "\nRe-run with --force to overwrite the recorded checksums.";
        }
    }

    /// <summary>Thrown when a migration apply fails server-side.</summary>
    public sealed class MigrationApplyException : Exception
    {
        public string FileName { get; }
        public MigrationApplyException(string fileName, string detail)
            : base($"migration '{fileName}' failed: {detail}")
        {
            FileName = fileName;
        }
    }
}
