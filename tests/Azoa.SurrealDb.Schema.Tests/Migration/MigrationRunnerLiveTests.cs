// SPDX-License-Identifier: UNLICENSED
// Azoa.SurrealDb.Schema.Tests -- Live migration round-trip.
//
// Acceptance gate: every committed .surql in Persistence/SurrealDb/Generated/Schemas/
// applies cleanly against a live SurrealDB instance, the schema_migration
// ledger lands one row per file, and re-running the same set is a clean
// no-op (every entry classified Skip).
//
// Targets the user-managed SurrealDB instance at localhost:8000 (root/root)
// using a dedicated namespace ("azoa_migration_test") and database
// ("schema_smoke") so production / dev data is never touched. The test
// drops the namespace via REMOVE NAMESPACE at the start of every run so
// repeated runs see a fresh slate.
//
// Skipped (early-return as Pass) when the local instance is unreachable
// -- matches the SkippableFact pattern used by the rest of the integration
// suite so CI without SurrealDB stays green.

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Azoa.SurrealDb.Schema.Cli;
using Azoa.SurrealDb.Schema.Migration;

namespace Azoa.SurrealDb.Schema.Tests.Migration
{
    /// <summary>
    /// Live integration tests for <see cref="MigrationRunner"/> against the
    /// local SurrealDB instance at <c>http://localhost:8000</c>. Tagged with
    /// the <c>Live</c> trait so CI lanes can opt-out via
    /// <c>--filter "Trait!=Live"</c>.
    /// </summary>
    [Trait("Category", "Live")]
    public class MigrationRunnerLiveTests
    {
        // Configured to match the user's running instance: surrealkv:///data/db
        // backing, root/root, port 8000. Single test database isolates from
        // any production / dev rows. The IPv4 literal sidesteps IPv6-localhost
        // resolution quirks on Windows where `localhost` can resolve to `::1`
        // and the SurrealDB server only listens on the IPv4 interface.
        private const string Endpoint = "http://127.0.0.1:8000";
        private const string User = "root";
        private const string Pass = "root";
        private const string TestNamespace = "azoa_migration_test";
        private const string TestDatabase = "schema_smoke";

        [Fact]
        public async Task Up_applies_all_generated_schemas_then_re_run_is_idempotent_noop()
        {
            // Skip cleanly when the local instance is not reachable -- CI
            // without SurrealDB should not fail this lane.
            if (!await IsServerReachableAsync())
            {
                Console.WriteLine($"[SKIP] SurrealDB not reachable at {Endpoint}");
                return;
            }

            // Locate the generated schemas directory by walking upward from
            // the test bin until we find Persistence/SurrealDb/Generated/Schemas.
            var schemasDir = ResolveSchemasDir();
            File.Exists(Path.Combine(schemasDir, "wallet.surql"))
                .Should().BeTrue($"expected canonical schema files under {schemasDir}");

            var files = MigrationRunner.DiscoverFiles(schemasDir);
            files.Should().NotBeEmpty(
                "the C#-first generator emits one .surql per [SurrealTable] POCO; an empty result means the test is pointing at the wrong directory.");

            using var conn = new HttpConnectionAdapter(Endpoint, User, Pass, TestNamespace, TestDatabase);
            await ResetNamespaceAsync(conn);

            // Configure the runner to bootstrap the NS+DB on first apply --
            // proves the "config namespace, create if missing" contract.
            var runner = new MigrationRunner(
                conn,
                appliedBy: "MigrationRunnerLiveTests",
                ensureNamespace: TestNamespace,
                ensureDatabase: TestDatabase);

            // First apply: every file is new -> Apply.
            var firstPlan = await runner.ApplyAsync(files);
            firstPlan.Should().HaveCount(files.Count);
            firstPlan.Select(p => p.Action)
                .Should().OnlyContain(a => a == MigrationAction.Apply,
                    "every file is new on a fresh namespace");

            // Ledger should now hold one row per .surql file.
            var status = await runner.StatusAsync();
            status.Should().HaveCount(files.Count);
            status.Select(s => s.FileName)
                .Should().BeEquivalentTo(files.Select(f => f.FileName));

            // Re-apply: every file's checksum matches -> Skip everywhere.
            // This is the canonical "DB already in sync" path; zero DDL
            // writes leave the runner on a no-op pass.
            var secondPlan = await runner.ApplyAsync(files);
            secondPlan.Select(p => p.Action)
                .Should().OnlyContain(a => a == MigrationAction.Skip,
                    "re-running an already-applied schema set must be a no-op (idempotent contract).");
        }

        [Fact]
        public async Task Up_applies_a_data_migration_after_the_schema_phase()
        {
            if (!await IsServerReachableAsync())
            {
                Console.WriteLine($"[SKIP] SurrealDB not reachable at {Endpoint}");
                return;
            }

            // Synthesise a one-shot migration: a tiny SELECT 1 that exercises
            // the runner's Phase-2 path without depending on any specific
            // schema-side row. The migration's only job is to prove the
            // ledger handles a Migrations/ entry the same way it handles
            // a Generated/Schemas/ entry.
            var schemasDir = ResolveSchemasDir();
            var migrationsDir = Path.Combine(Path.GetTempPath(),
                "azoa_migration_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(migrationsDir);
            try
            {
                var fakeMigrationName = "20260605_120000__noop_test_migration.surql";
                File.WriteAllText(
                    Path.Combine(migrationsDir, fakeMigrationName),
                    // Pure no-op statement that SurrealDB accepts on any
                    // namespace. RETURN <value>; is the idiomatic "no-op"
                    // surql -- SELECT requires FROM in SurrealDB 1.5+.
                    "RETURN 1;\n");

                using var conn = new HttpConnectionAdapter(Endpoint, User, Pass, TestNamespace, TestDatabase);
                await ResetNamespaceAsync(conn);

                var runner = new MigrationRunner(
                    conn,
                    appliedBy: "MigrationRunnerLiveTests",
                    ensureNamespace: TestNamespace,
                    ensureDatabase: TestDatabase);
                var schemaFiles = MigrationRunner.DiscoverFiles(schemasDir);
                var migrationFiles = MigrationRunner.DiscoverFiles(migrationsDir);

                await runner.ApplyAsync(schemaFiles);
                var migrPlan = await runner.ApplyAsync(migrationFiles);

                migrPlan.Should().HaveCount(1);
                migrPlan[0].Action.Should().Be(MigrationAction.Apply);
                migrPlan[0].File.FileName.Should().Be(fakeMigrationName);

                var status = await runner.StatusAsync();
                status.Select(s => s.FileName).Should().Contain(fakeMigrationName);
            }
            finally
            {
                try { Directory.Delete(migrationsDir, recursive: true); } catch { /* best-effort */ }
            }
        }

        // ─── helpers ──────────────────────────────────────────────────────

        private static async Task<bool> IsServerReachableAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var resp = await http.GetAsync(Endpoint + "/health");
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Drop + recreate the test namespace so every run starts from a clean
        /// slate. The runner uses Surreal-NS/DB headers from the adapter, so
        /// REMOVE NAMESPACE has to be issued via a system-level (no-NS)
        /// connection.
        /// </summary>
        private static async Task ResetNamespaceAsync(HttpConnectionAdapter conn)
        {
            // REMOVE NAMESPACE is namespace-scoped -- it requires no inner
            // namespace context. The HTTP transport sets the headers
            // automatically; SurrealDB tolerates REMOVE NAMESPACE inside a
            // header-scoped session (it just clears + recreates).
            var ddl =
                "REMOVE NAMESPACE IF EXISTS " + TestNamespace + ";\n" +
                "DEFINE NAMESPACE " + TestNamespace + ";\n" +
                "USE NS " + TestNamespace + ";\n" +
                "DEFINE DATABASE " + TestDatabase + ";\n";
            var result = await conn.ExecuteAsync(ddl, CancellationToken.None);
            // Tolerant: REMOVE NAMESPACE IF EXISTS may return a server-side
            // "not found" notice that surfaces as ERR on older builds. The
            // subsequent DEFINE NAMESPACE is the load-bearing step.
            if (!result.IsOk)
            {
                throw new InvalidOperationException(
                    "could not reset test namespace: " + (result.Detail ?? "unknown"));
            }
        }

        private static string ResolveSchemasDir()
        {
            var probe = AppContext.BaseDirectory;
            for (int hop = 0; hop < 12; hop++)
            {
                var candidate = Path.Combine(probe, "Persistence", "SurrealDb", "Generated", "Schemas");
                if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
                var parent = Directory.GetParent(probe);
                if (parent == null) break;
                probe = parent.FullName;
            }
            throw new DirectoryNotFoundException(
                "could not locate Persistence/SurrealDb/Generated/Schemas by walking up from " + AppContext.BaseDirectory);
        }
    }
}
