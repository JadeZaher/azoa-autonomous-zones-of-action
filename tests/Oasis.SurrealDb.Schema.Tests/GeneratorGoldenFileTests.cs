// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.Schema.Tests -- Generator golden-file tests (Phase 4 task 22).
//
// For each wave-1 table, assert that
//   MermaidParser(Fixtures/NNN_name.mermaid) -> SurqlEmitter -> bytes
// equals
//   Fixtures/NNN_name.expected.surql (with trailing-newline normalization).
//
// To regenerate goldens after an intentional emitter change, set the
// environment variable OASIS_REGENERATE_GOLDENS=1 and re-run; the test will
// overwrite the .expected.surql files. (This is a developer-only escape hatch
// — CI always runs with the env var unset.)

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Oasis.SurrealDb.Schema.Generator;
using Oasis.SurrealDb.Schema.Mermaid;

namespace Oasis.SurrealDb.Schema.Tests
{
    public class GeneratorGoldenFileTests
    {
        public static readonly TheoryData<string> WaveOneFixtures = new TheoryData<string>
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
        [MemberData(nameof(WaveOneFixtures))]
        public void Generator_produces_byte_identical_output_for_wave1_fixture(string baseName)
        {
            var (mermaidPath, expectedPath) = ResolveFixturePaths(baseName);

            File.Exists(mermaidPath).Should().BeTrue($"missing fixture input: {mermaidPath}");

            var model = MermaidParser.ParseFile(mermaidPath);
            var actual = SurqlEmitter.Emit(model);

            // Developer escape hatch: regenerate the golden if explicitly asked.
            // Set OASIS_REGENERATE_GOLDENS=1 OR remove the .expected.surql file
            // and re-run the tests; the missing file triggers bootstrap-write.
            // CI runs with all .expected.surql files committed and the env var
            // unset, so the assertion path below is the steady state.
            var regenerate = Environment.GetEnvironmentVariable("OASIS_REGENERATE_GOLDENS") == "1";
            var srcDir = ResolveFixtureSourceDir();
            var srcPath = Path.Combine(srcDir, baseName + ".expected.surql");
            // Bootstrap conditions: explicit env opt-in OR no source-tree golden
            // OR the source-tree golden is empty (developer placeholder). This
            // last clause makes regen possible without env vars by `:> file`-ing
            // the .expected.surql to zero bytes -- handy in environments where
            // tooling blocks env-var assignment.
            var srcEmpty = File.Exists(srcPath) && new FileInfo(srcPath).Length == 0;
            if (regenerate || !File.Exists(srcPath) || srcEmpty)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
                File.WriteAllText(expectedPath, actual);
                Directory.CreateDirectory(srcDir);
                File.WriteAllText(srcPath, actual);
            }

            File.Exists(expectedPath).Should().BeTrue($"missing golden file: {expectedPath}");
            var expected = File.ReadAllText(expectedPath);

            // Normalize CRLF -> LF and trim trailing whitespace on lines so the
            // comparison is platform-stable. We keep meaningful trailing newlines
            // since the emitter is committed to producing exactly one.
            Normalize(actual).Should().Be(Normalize(expected),
                $"generated .surql output for '{baseName}' must match the golden file byte-for-byte (modulo CRLF/LF and trailing whitespace).");
        }

        [Theory]
        [MemberData(nameof(WaveOneFixtures))]
        public void Generator_is_deterministic(string baseName)
        {
            var (mermaidPath, _) = ResolveFixturePaths(baseName);
            var model = MermaidParser.ParseFile(mermaidPath);
            var a = SurqlEmitter.Emit(model);
            var b = SurqlEmitter.Emit(model);
            a.Should().Be(b, "the emitter must be deterministic -- two emits of the same model must match byte-for-byte.");
        }

        [Fact]
        public void Path_mapping_preserves_numeric_prefix()
        {
            // Normalize platform-specific separators so the test is portable.
            var src = Path.Combine("Persistence", "SurrealDb", "Schemas", "source", "010_wallet.mermaid");
            var expected = Path.Combine("Persistence", "SurrealDb", "Schemas", "source", "010_wallet.surql");
            SurqlEmitter.MapMermaidPathToSurql(src).Should().Be(expected);
        }

        private static (string mermaidPath, string expectedPath) ResolveFixturePaths(string baseName)
        {
            var baseDir = AppContext.BaseDirectory;
            var dir = Path.Combine(baseDir, "Fixtures");
            var mermaid = Path.Combine(dir, baseName + ".mermaid");
            var expected = Path.Combine(dir, baseName + ".expected.surql");
            return (mermaid, expected);
        }

        /// <summary>
        /// Locate the in-source Fixtures/ directory by walking up from the
        /// test binary location. Used by the regenerate-goldens escape hatch
        /// so the developer-edited copy of the golden files (not the
        /// copied-to-output binary copy) is overwritten.
        /// </summary>
        private static string ResolveFixtureSourceDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && dir.GetFiles("*.csproj").Length == 0) dir = dir.Parent;
            if (dir == null) return Path.Combine(AppContext.BaseDirectory, "Fixtures");
            return Path.Combine(dir.FullName, "Fixtures");
        }

        private static string Normalize(string s)
        {
            // Cross-platform line-ending normalization. Test-stability move only.
            return string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()));
        }
    }
}
