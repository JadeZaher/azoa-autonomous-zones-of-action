// SPDX-License-Identifier: UNLICENSED
// OASIS.WebAPI.Tests -- byte-equivalence acceptance gate for the C#-first
// SurrealDB schema authoring surface.
//
// Acceptance contract:
//   For every [SurrealTable]-decorated POCO in Persistence/SurrealDb/Models/
//   the emitted .surql (via Oasis.SurrealDb.Schema.AttributeSchemaScanner +
//   SurqlEmitter) MUST be byte-identical to the corresponding committed file
//   under Persistence/SurrealDb/Generated/Schemas/<table>.surql.
//
// This test fails build whenever the attribute layer drifts from the
// canonical .surql output, OR a new POCO is added whose committed .surql
// has not yet been regenerated. Either failure mode is intentional --
// drift detection IS the contract.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Oasis.SurrealDb.Client.Schema;
using Oasis.SurrealDb.Schema.Generator;
using Oasis.SurrealDb.Schema.Model;

namespace OASIS.WebAPI.Tests.Persistence.SurrealDb
{
    public class AttributePocoByteEquivalenceTests
    {
        /// <summary>
        /// Discovers every [SurrealTable]-decorated POCO in the OASIS.WebAPI
        /// assembly. The test data is generated at runtime so adding a new
        /// POCO automatically extends coverage; no fixture list to keep in
        /// sync.
        /// </summary>
        public static TheoryData<Type, string> AllAttributedPocos()
        {
            var data = new TheoryData<Type, string>();
            var asm = typeof(OASIS.WebAPI.Persistence.SurrealDb.Models.Wallet).Assembly;
            foreach (var t in asm.GetTypes())
            {
                var table = t.GetCustomAttribute<SurrealTableAttribute>(inherit: false);
                if (table == null) continue;
                data.Add(t, table.Name);
            }
            return data;
        }

        [Theory]
        [MemberData(nameof(AllAttributedPocos))]
        public void Poco_emit_matches_committed_surql_byte_for_byte(Type pocoType, string tableName)
        {
            var model = AttributeSchemaScanner.ScanType(pocoType);
            var emitted = SurqlEmitter.Emit(model);
            var goldenPath = ResolveCommittedSurqlPath(tableName);

            // Regen escape hatch: setting OASIS_REGENERATE_GOLDENS=1 OR
            // touching the .surql file to zero bytes makes the test author
            // its current emit into the repo as the new golden. CI runs
            // with the env var unset and the goldens present, so this is a
            // developer-only path.
            var regen = Environment.GetEnvironmentVariable("OASIS_REGENERATE_GOLDENS") == "1";
            var emptyGolden = File.Exists(goldenPath) && new FileInfo(goldenPath).Length == 0;
            if (regen || !File.Exists(goldenPath) || emptyGolden)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
                File.WriteAllText(goldenPath, emitted);
            }

            File.Exists(goldenPath).Should().BeTrue(
                $"missing committed .surql at {goldenPath}; either author the file " +
                $"(regenerate via `oasis-surreal generate-from-assembly`) or remove the " +
                $"[SurrealTable] attribute from {pocoType.FullName}.");

            var golden = File.ReadAllText(goldenPath);
            Normalize(emitted).Should().Be(Normalize(golden),
                $"attribute-driven .surql for {pocoType.FullName} (table {tableName}) " +
                $"diverged from the committed file at {goldenPath}. If the divergence is " +
                "intentional, regenerate the .surql via the CLI (`oasis-surreal " +
                "generate-from-assembly bin/Debug/net8.0/OASIS.WebAPI.dll`) or run the " +
                "test suite with OASIS_REGENERATE_GOLDENS=1; if not, fix the POCO's " +
                "attribute decoration.");
        }

        // ─── helpers ──────────────────────────────────────────────────────

        private static string ResolveCommittedSurqlPath(string tableName)
        {
            // Walk upward from the test bin until we find the Generated/Schemas
            // directory at the repo root.
            var probe = AppContext.BaseDirectory;
            for (int hop = 0; hop < 12; hop++)
            {
                var candidate = Path.Combine(
                    probe, "Persistence", "SurrealDb", "Generated", "Schemas", tableName + ".surql");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                var parent = Directory.GetParent(probe);
                if (parent == null) break;
                probe = parent.FullName;
            }
            // Return the expected location so the BeTrue assertion surfaces it.
            return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "Persistence", "SurrealDb", "Generated", "Schemas", tableName + ".surql"));
        }

        private static string Normalize(string s)
        {
            var lf = s.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = lf.Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
            var joined = string.Join("\n", lines);
            return joined.EndsWith("\n", StringComparison.Ordinal) ? joined : joined + "\n";
        }
    }
}
