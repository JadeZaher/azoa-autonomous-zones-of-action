// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.Schema.Tests -- in-repo source/.mermaid vs .surql sync gate.
//
// This test enforces the surrealdb-client-package Phase 6 + spec acceptance
// criterion: every committed .surql under Persistence/SurrealDb/Schemas/
// must be the byte-identical output of regenerating from its sibling
// .mermaid source under Persistence/SurrealDb/Schemas/source/.
//
// If you intentionally edit a schema:
//   1. Edit the .mermaid source -- this is the source of truth.
//   2. Run `dotnet run --project packages/Oasis.SurrealDb.Schema
//          -- generate Persistence/SurrealDb/Schemas/source/<name>.mermaid
//          --out Persistence/SurrealDb/Schemas/<name>.surql`
//   3. Re-run this test. It MUST stay green.
//
// If you cannot edit a .mermaid (e.g. ad-hoc schema patch), this test fails
// on purpose -- the .surql is meant to be a build artifact, not a source.

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Oasis.SurrealDb.Schema.Generator;
using Oasis.SurrealDb.Schema.Mermaid;

namespace Oasis.SurrealDb.Schema.Tests
{
    public class WaveOneInRepoSyncTests
    {
        public static readonly TheoryData<string> WaveOneTables = new TheoryData<string>
        {
            "010_wallet",
            "020_bridge_tx",
            "030_swap_state",
            "040_nft_ownership",
            "050_operation_log",
            "060_consumed_vaa_ledger",
            "070_idempotency_key_store",
        };

        [Theory]
        [MemberData(nameof(WaveOneTables))]
        public void In_repo_surql_matches_generator_output_for_mermaid_source(string baseName)
        {
            var repoRoot = ResolveRepoRoot();
            var mermaidPath = Path.Combine(repoRoot, "Persistence", "SurrealDb", "Schemas", "source",
                baseName + ".mermaid");
            var surqlPath = Path.Combine(repoRoot, "Persistence", "SurrealDb", "Schemas",
                baseName + ".surql");

            File.Exists(mermaidPath).Should().BeTrue(
                $"missing source-of-truth Mermaid file: {mermaidPath}. Every wave-1 .surql " +
                "must have a sibling .mermaid under Persistence/SurrealDb/Schemas/source/.");
            File.Exists(surqlPath).Should().BeTrue(
                $"missing committed .surql at canonical location: {surqlPath}. Regenerate via " +
                "`dotnet run --project packages/Oasis.SurrealDb.Schema -- generate {source} --out {dest}`.");

            var model = MermaidParser.ParseFile(mermaidPath);
            var generated = SurqlEmitter.Emit(model);
            var committed = File.ReadAllText(surqlPath);

            Normalize(generated).Should().Be(Normalize(committed),
                $"in-repo .surql for '{baseName}' has drifted from the .mermaid source. " +
                "The .surql files are build artifacts -- regenerate from source/<name>.mermaid via " +
                $"`dotnet run --project packages/Oasis.SurrealDb.Schema -- generate Persistence/SurrealDb/Schemas/source/{baseName}.mermaid --out Persistence/SurrealDb/Schemas/{baseName}.surql`.");
        }

        private static string ResolveRepoRoot()
        {
            // Walk up from the test binary's directory until we find OASIS.WebAPI.csproj at the repo root.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "OASIS.WebAPI.csproj")))
            {
                dir = dir.Parent;
            }
            if (dir == null)
            {
                throw new InvalidOperationException(
                    "Could not locate repo root (looked for OASIS.WebAPI.csproj walking up from " +
                    $"{AppContext.BaseDirectory}). Run tests from inside the repository tree.");
            }
            return dir.FullName;
        }

        private static string Normalize(string s)
        {
            // Cross-platform line-ending normalization + trailing-whitespace trim, identical to
            // GeneratorGoldenFileTests.Normalize so both gates use the same comparison.
            return string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()));
        }
    }
}
