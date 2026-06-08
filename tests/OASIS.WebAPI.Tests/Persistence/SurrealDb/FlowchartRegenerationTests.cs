// SPDX-License-Identifier: UNLICENSED
// OASIS.WebAPI.Tests -- per-slice + master graph-LR flowchart regeneration.
//
// Runs every test build: scans the OASIS.WebAPI assembly for
// [SurrealTable]-decorated POCOs, projects them onto the schema IR, and
// rewrites Persistence/SurrealDb/Generated/Flowcharts/ with the current
// emit. Deterministic emit means a clean working tree after the test run
// = no drift; a dirty working tree after CI = the POCO surface changed
// without regenerating the diagrams.

using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Oasis.SurrealDb.Client.Schema;
using Oasis.SurrealDb.Schema.Generator;

namespace OASIS.WebAPI.Tests.Persistence.SurrealDb
{
    public class FlowchartRegenerationTests
    {
        [Fact]
        public void Flowchart_outputs_regenerate_from_attribute_scan_without_drift()
        {
            var asm = typeof(OASIS.WebAPI.Persistence.SurrealDb.Models.Wallet).Assembly;
            var pocos = asm.GetTypes()
                .Where(t => t.GetCustomAttribute<SurrealTableAttribute>(inherit: false) != null)
                .ToList();
            pocos.Should().NotBeEmpty();

            var combined = AttributeSchemaScanner.ScanTypes(pocos);
            var result = MermaidFlowchartEmitter.EmitFromAttributeScan(new[] { combined });

            var outDir = ResolveFlowchartDir();
            Directory.CreateDirectory(outDir);
            foreach (var kvp in result.SliceFiles)
            {
                File.WriteAllText(Path.Combine(outDir, kvp.Key), kvp.Value);
            }
            File.WriteAllText(Path.Combine(outDir, "domain.flowchart.mermaid"), result.MasterFlowchart);

            result.SliceFiles.Should().NotBeEmpty();
            result.MasterFlowchart.Should().Contain("graph LR");
            result.MasterFlowchart.Should().Contain("classDef nodeClass");
        }

        private static string ResolveFlowchartDir()
        {
            var probe = System.AppContext.BaseDirectory;
            for (int hop = 0; hop < 12; hop++)
            {
                var candidate = Path.Combine(
                    probe, "Persistence", "SurrealDb", "Generated", "Flowcharts");
                if (Directory.Exists(Path.GetDirectoryName(candidate)!)) return Path.GetFullPath(candidate);
                var parent = Directory.GetParent(probe);
                if (parent == null) break;
                probe = parent.FullName;
            }
            return Path.GetFullPath(Path.Combine(
                System.AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "Persistence", "SurrealDb", "Generated", "Flowcharts"));
        }
    }
}
