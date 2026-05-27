// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.Schema -- `oasis-surreal` CLI tool entry point (Phase 4 task 24).
//
// Subcommands:
//   oasis-surreal migrate up        Apply pending .surql files
//   oasis-surreal migrate down      (stub) Refuse with non-zero — manual rollback only
//   oasis-surreal migrate status    Read schema_migration table
//   oasis-surreal migrate dry-run   Plan only; zero writes
//   oasis-surreal generate <file>   Mermaid source -> .surql sibling
//   oasis-surreal validate <file>   Parse + report errors; exit non-zero on fail
//   oasis-surreal aggregates        Emit per-slice + master Mermaid diagrams from source/*.mermaid
//
// Connection config sources (resolution order, first wins per field):
//   1. --connection / --user / --pass / --namespace / --database flags
//   2. OASIS_SURREAL_URL / _USER / _PASS / _NS / _DB env vars
//   3. (no defaults — failing fast on missing fields when a command requires them)

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oasis.SurrealDb.Schema.Cli;
using Oasis.SurrealDb.Schema.Generator;
using Oasis.SurrealDb.Schema.Mermaid;
using Oasis.SurrealDb.Schema.Migration;

namespace Oasis.SurrealDb.Schema
{
    /// <summary>CLI entry point. Returns OS exit code.</summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            try
            {
                var cli = CliArgs.Parse(args);
                if (string.IsNullOrEmpty(cli.Command) || cli.HasFlag("help") || cli.Command == "help")
                {
                    PrintHelp();
                    return 0;
                }

                switch (cli.Command)
                {
                    case "migrate":
                        return await RunMigrateAsync(cli).ConfigureAwait(false);
                    case "generate":
                        return RunGenerate(cli);
                    case "validate":
                        return RunValidate(cli);
                    case "aggregates":
                        return RunAggregates(cli);
                    default:
                        Console.Error.WriteLine($"unknown command: '{cli.Command}'");
                        PrintHelp();
                        return 64; // EX_USAGE
                }
            }
            catch (MermaidParseException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            catch (MigrationChecksumMismatchException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            catch (MigrationApplyException ex)
            {
                Console.Error.WriteLine("apply failed: " + ex.Message);
                return 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 70; // EX_SOFTWARE
            }
        }

        // ── migrate ────────────────────────────────────────────────────────
        private static async Task<int> RunMigrateAsync(CliArgs cli)
        {
            var sub = cli.SubCommand;
            switch (sub)
            {
                case "down":
                    Console.Error.WriteLine("migrate down: not implemented; manual rollback only.");
                    return 0;
                case "up":
                case "status":
                case "dry-run":
                    break;
                default:
                    Console.Error.WriteLine($"unknown migrate subcommand: '{sub}' (expected up|down|status|dry-run)");
                    return 64;
            }

            var schemaDir = cli.Flag("dir") ?? "Persistence/SurrealDb/Schemas";
            var conn = BuildConnection(cli);
            if (conn == null) return 64;
            var runner = new MigrationRunner(conn, cli.Flag("applied-by"));

            try
            {
                if (sub == "status")
                {
                    var rows = await runner.StatusAsync().ConfigureAwait(false);
                    if (rows.Count == 0) Console.WriteLine("(no migrations applied)");
                    foreach (var r in rows)
                    {
                        Console.WriteLine($"{r.FileName}\t{r.Checksum}\t{r.AppliedAt}\t{r.AppliedBy}");
                    }
                    return 0;
                }

                var files = MigrationRunner.DiscoverFiles(schemaDir);
                if (files.Count == 0)
                {
                    Console.Error.WriteLine($"no .surql files found under {schemaDir}");
                    return 1;
                }

                bool dryRun = sub == "dry-run" || cli.HasFlag("dry-run");
                bool force = cli.HasFlag("force");
                var plan = await runner.ApplyAsync(files, dryRun, force).ConfigureAwait(false);

                foreach (var p in plan)
                {
                    var verb = p.Action switch
                    {
                        MigrationAction.Apply => dryRun ? "WOULD APPLY" : "APPLIED",
                        MigrationAction.Skip => "SKIP (checksum match)",
                        MigrationAction.ChecksumMismatch => "MISMATCH",
                        _ => p.Action.ToString(),
                    };
                    Console.WriteLine($"{verb}\t{p.File.FileName}\t{p.File.Checksum}");
                }
                return 0;
            }
            finally
            {
                (conn as IDisposable)?.Dispose();
            }
        }

        // ── generate ───────────────────────────────────────────────────────
        private static int RunGenerate(CliArgs cli)
        {
            if (cli.Positionals.Count == 0 && string.IsNullOrEmpty(cli.SubCommand))
            {
                Console.Error.WriteLine("usage: oasis-surreal generate <file.mermaid> [--out <path>]");
                return 64;
            }
            var inputPath = cli.Positionals.Count > 0 ? cli.Positionals[0] : cli.SubCommand;
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"file not found: {inputPath}");
                return 1;
            }
            var model = MermaidParser.ParseFile(inputPath);
            var output = SurqlEmitter.Emit(model);
            var outPath = cli.Flag("out") ?? SurqlEmitter.MapMermaidPathToSurql(inputPath);
            File.WriteAllText(outPath, output);
            Console.WriteLine($"wrote {outPath} ({output.Length} bytes)");
            return 0;
        }

        // ── aggregates ─────────────────────────────────────────────────────
        private static int RunAggregates(CliArgs cli)
        {
            var source = cli.Flag("source") ?? "Persistence/SurrealDb/Schemas/source";
            var output = cli.Flag("out") ?? "docs";
            if (!Directory.Exists(source))
            {
                Console.Error.WriteLine($"source directory not found: {source}");
                return 1;
            }
            var result = AggregateEmitter.EmitToDirectory(source, output);
            Console.WriteLine($"wrote {result.SliceFiles.Count} slice file(s) to {Path.Combine(output, "aggregates")}");
            foreach (var slice in result.SliceNames)
            {
                Console.WriteLine($"  - {slice}.mermaid");
            }
            Console.WriteLine($"wrote master diagram to {Path.Combine(output, "domain.generated.mermaid")}");
            if (result.UnassignedEntities.Count > 0)
            {
                Console.Error.WriteLine($"warning: {result.UnassignedEntities.Count} entit(y/ies) without @surreal.slice annotation -- bucketed as '_unassigned':");
                foreach (var name in result.UnassignedEntities)
                {
                    Console.Error.WriteLine($"  - {name}");
                }
            }
            return 0;
        }

        // ── validate ───────────────────────────────────────────────────────
        private static int RunValidate(CliArgs cli)
        {
            if (cli.Positionals.Count == 0 && string.IsNullOrEmpty(cli.SubCommand))
            {
                Console.Error.WriteLine("usage: oasis-surreal validate <file.mermaid>");
                return 64;
            }
            var inputPath = cli.Positionals.Count > 0 ? cli.Positionals[0] : cli.SubCommand;
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"file not found: {inputPath}");
                return 1;
            }
            var model = MermaidParser.ParseFile(inputPath);
            Console.WriteLine($"ok: {inputPath} -- {model.Entities.Count} entit(y/ies), {model.Relationships.Count} relationship(s)");
            return 0;
        }

        // ── helpers ────────────────────────────────────────────────────────
        private static ISurrealConnection? BuildConnection(CliArgs cli)
        {
            var url = cli.Flag("connection") ?? Environment.GetEnvironmentVariable("OASIS_SURREAL_URL");
            var user = cli.Flag("user") ?? Environment.GetEnvironmentVariable("OASIS_SURREAL_USER");
            var pass = cli.Flag("pass") ?? Environment.GetEnvironmentVariable("OASIS_SURREAL_PASS");
            var ns = cli.Flag("namespace") ?? Environment.GetEnvironmentVariable("OASIS_SURREAL_NS");
            var db = cli.Flag("database") ?? Environment.GetEnvironmentVariable("OASIS_SURREAL_DB");

            if (string.IsNullOrWhiteSpace(url))
            {
                Console.Error.WriteLine("missing connection URL. Set --connection or OASIS_SURREAL_URL.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(db))
            {
                Console.Error.WriteLine("missing namespace/database. Set --namespace / --database or OASIS_SURREAL_NS / _DB.");
                return null;
            }

            return new HttpConnectionAdapter(url, user ?? "", pass ?? "", ns!, db!);
        }

        private static void PrintHelp()
        {
            Console.WriteLine("oasis-surreal -- SurrealDB schema CLI");
            Console.WriteLine();
            Console.WriteLine("  oasis-surreal migrate up        Apply pending .surql files");
            Console.WriteLine("  oasis-surreal migrate down      (stub) Refuses; manual rollback only");
            Console.WriteLine("  oasis-surreal migrate status    Read schema_migration table");
            Console.WriteLine("  oasis-surreal migrate dry-run   Plan only; zero writes");
            Console.WriteLine("  oasis-surreal generate <file>   Mermaid source -> .surql sibling");
            Console.WriteLine("  oasis-surreal validate <file>   Parse + report errors");
            Console.WriteLine("  oasis-surreal aggregates        Emit per-slice + master Mermaid diagrams");
            Console.WriteLine();
            Console.WriteLine("Aggregates flags:");
            Console.WriteLine("  --source <dir>   Source directory (default: Persistence/SurrealDb/Schemas/source)");
            Console.WriteLine("  --out <dir>      Output root (default: docs; emits docs/aggregates/*.mermaid + docs/domain.generated.mermaid)");
            Console.WriteLine();
            Console.WriteLine("Connection flags / env vars (env in parens):");
            Console.WriteLine("  --connection  (OASIS_SURREAL_URL)   http(s)://host:port");
            Console.WriteLine("  --user        (OASIS_SURREAL_USER)");
            Console.WriteLine("  --pass        (OASIS_SURREAL_PASS)");
            Console.WriteLine("  --namespace   (OASIS_SURREAL_NS)");
            Console.WriteLine("  --database    (OASIS_SURREAL_DB)");
            Console.WriteLine();
            Console.WriteLine("Migrate flags:");
            Console.WriteLine("  --dir <path>     Directory of .surql files (default: Persistence/SurrealDb/Schemas)");
            Console.WriteLine("  --force          Overwrite recorded checksum on mismatch");
            Console.WriteLine("  --dry-run        Plan without writing (equivalent to subcommand dry-run)");
            Console.WriteLine("  --applied-by <s> Identity recorded in schema_migration.applied_by");
        }
    }
}
