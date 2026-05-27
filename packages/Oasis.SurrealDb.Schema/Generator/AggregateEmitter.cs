// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.Schema -- Aggregate slice diagram emitter (RUNBOOK §4 Phase B).
//
// Multiple MermaidSchemaModel inputs (one per source .mermaid file) -->
//   one per-slice .mermaid file + one concatenated master diagram
//
// Determinism: source files are sorted by path before grouping so the
// emitted slice/master byte sequence is stable across machines.
//
// Slice membership comes from the entity-level `%% @surreal.slice "<name>"`
// annotation (registered in MermaidParser.KnownDirectives). Entities
// without a slice annotation, or annotated with the literal "_skip",
// are excluded from the visualization (index-only pseudo-entities like
// the 240_hnsw_indexes file use this).
//
// Relationships are real Mermaid arrow lines on the source files. A
// relationship is included in a slice when BOTH endpoints belong to
// that slice. The master diagram contains every entity + every
// relationship.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Oasis.SurrealDb.Schema.Mermaid;

namespace Oasis.SurrealDb.Schema.Generator
{
    /// <summary>
    /// Emits per-aggregate Mermaid slice diagrams + a concatenated master
    /// from a set of parsed source <see cref="MermaidSchemaModel"/>s.
    /// Pure function — no I/O.
    /// </summary>
    public static class AggregateEmitter
    {
        /// <summary>Special slice value that means "exclude from visualization."</summary>
        public const string SkipSliceName = "_skip";

        /// <summary>Slice name applied to entities without an explicit `@surreal.slice` annotation.</summary>
        public const string UnassignedSliceName = "_unassigned";

        /// <summary>Result of an emit pass — keyed by output filename relative to the output root.</summary>
        public sealed class EmitResult
        {
            /// <summary>per-slice files. Key: <c>"&lt;slice&gt;.mermaid"</c>. Value: file contents.</summary>
            public IReadOnlyDictionary<string, string> SliceFiles { get; }

            /// <summary>The concatenated master diagram contents.</summary>
            public string MasterDiagram { get; }

            /// <summary>Slices in deterministic order (sorted by name).</summary>
            public IReadOnlyList<string> SliceNames { get; }

            /// <summary>Entities seen without an `@surreal.slice` annotation. Empty list = clean run.</summary>
            public IReadOnlyList<string> UnassignedEntities { get; }

            public EmitResult(
                IReadOnlyDictionary<string, string> sliceFiles,
                string masterDiagram,
                IReadOnlyList<string> sliceNames,
                IReadOnlyList<string> unassignedEntities)
            {
                SliceFiles = sliceFiles;
                MasterDiagram = masterDiagram;
                SliceNames = sliceNames;
                UnassignedEntities = unassignedEntities;
            }
        }

        /// <summary>
        /// Group entities by their <c>@surreal.slice</c> annotation, emit
        /// one slice file per group + a master diagram concatenating all
        /// non-skipped entities and relationships.
        /// </summary>
        public static EmitResult Emit(IEnumerable<MermaidSchemaModel> sources)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));

            // Stable input ordering: sort by source path so two runs over
            // the same directory produce byte-identical output.
            var ordered = sources
                .OrderBy(m => m.SourceFile, StringComparer.Ordinal)
                .ToList();

            // Group entities by slice; collect skipped + unassigned.
            var bySlice = new SortedDictionary<string, List<MermaidEntity>>(StringComparer.Ordinal);
            var skipped = new HashSet<string>(StringComparer.Ordinal);
            var unassigned = new List<string>();

            foreach (var model in ordered)
            {
                foreach (var entity in model.Entities)
                {
                    var slice = ReadSliceAnnotation(entity);
                    if (string.Equals(slice, SkipSliceName, StringComparison.Ordinal))
                    {
                        skipped.Add(entity.Name);
                        continue;
                    }
                    if (slice == null)
                    {
                        unassigned.Add(entity.Name);
                        slice = UnassignedSliceName;
                    }
                    if (!bySlice.TryGetValue(slice, out var bucket))
                    {
                        bucket = new List<MermaidEntity>();
                        bySlice[slice] = bucket;
                    }
                    bucket.Add(entity);
                }
            }

            // Collect every relationship + its (FromEntity, ToEntity, slice-of-both)
            // assignment. A relationship belongs to a slice when both endpoints
            // belong to that slice; cross-slice relationships are emitted only
            // on the master diagram.
            var allRelationships = new List<MermaidRelationship>();
            foreach (var model in ordered)
            {
                foreach (var rel in model.Relationships)
                {
                    if (skipped.Contains(rel.FromEntity) || skipped.Contains(rel.ToEntity)) continue;
                    allRelationships.Add(rel);
                }
            }

            // Build entity -> slice lookup once (sorted, deterministic).
            var entitySlice = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kvp in bySlice)
            {
                foreach (var e in kvp.Value)
                {
                    entitySlice[e.Name] = kvp.Key;
                }
            }

            var sliceFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var kvp in bySlice)
            {
                var sliceName = kvp.Key;
                var sliceEntities = kvp.Value;
                var sliceRelationships = allRelationships
                    .Where(r =>
                        entitySlice.TryGetValue(r.FromEntity, out var fs) && fs == sliceName &&
                        entitySlice.TryGetValue(r.ToEntity, out var ts) && ts == sliceName)
                    .ToList();

                var contents = RenderDiagram(
                    title: "Slice: " + sliceName,
                    entities: sliceEntities,
                    relationships: sliceRelationships,
                    includeFieldDetail: true);
                sliceFiles[sliceName + ".mermaid"] = contents;
            }

            // Master diagram: every non-skipped entity + every non-skipped relationship,
            // grouped visually by slice via subgraph-style comments.
            var master = RenderMasterDiagram(bySlice, allRelationships);

            return new EmitResult(
                sliceFiles: sliceFiles,
                masterDiagram: master,
                sliceNames: bySlice.Keys.ToList(),
                unassignedEntities: unassigned);
        }

        /// <summary>
        /// Read source files from <paramref name="sourceDir"/>, emit slices
        /// to <paramref name="outDir"/>/aggregates/&lt;slice&gt;.mermaid and the
        /// master to <paramref name="outDir"/>/domain.generated.mermaid.
        /// Returns the emit result for caller diagnostics.
        /// </summary>
        public static EmitResult EmitToDirectory(string sourceDir, string outDir)
        {
            if (sourceDir == null) throw new ArgumentNullException(nameof(sourceDir));
            if (outDir == null) throw new ArgumentNullException(nameof(outDir));
            if (!Directory.Exists(sourceDir))
            {
                throw new DirectoryNotFoundException("source directory not found: " + sourceDir);
            }
            var aggregatesDir = Path.Combine(outDir, "aggregates");
            Directory.CreateDirectory(aggregatesDir);

            var sources = Directory.GetFiles(sourceDir, "*.mermaid", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(MermaidParser.ParseFile)
                .ToList();

            var result = Emit(sources);

            foreach (var kvp in result.SliceFiles)
            {
                File.WriteAllText(Path.Combine(aggregatesDir, kvp.Key), kvp.Value);
            }
            File.WriteAllText(Path.Combine(outDir, "domain.generated.mermaid"), result.MasterDiagram);

            return result;
        }

        // ── helpers ────────────────────────────────────────────────────────

        private static string? ReadSliceAnnotation(MermaidEntity entity)
        {
            for (int i = 0; i < entity.Annotations.Count; i++)
            {
                var a = entity.Annotations[i];
                if (string.Equals(a.Directive, "slice", StringComparison.Ordinal))
                {
                    // Slice annotation is a positional string argument; the parser
                    // strips the surrounding quotes and stores in RawArguments.
                    // RawArguments may include the quoted form, so unquote.
                    var raw = a.RawArguments.Trim();
                    if (raw.StartsWith("\"", StringComparison.Ordinal) &&
                        raw.EndsWith("\"", StringComparison.Ordinal) &&
                        raw.Length >= 2)
                    {
                        return raw.Substring(1, raw.Length - 2);
                    }
                    return raw;
                }
            }
            return null;
        }

        private static string RenderDiagram(
            string title,
            IReadOnlyList<MermaidEntity> entities,
            IReadOnlyList<MermaidRelationship> relationships,
            bool includeFieldDetail)
        {
            var sb = new StringBuilder();
            sb.AppendLine("%% " + title);
            sb.AppendLine("%% GENERATED FILE -- do not edit. Source: Persistence/SurrealDb/Schemas/source/*.mermaid");
            sb.AppendLine("%% Regenerate with: dotnet run --project packages/Oasis.SurrealDb.Schema -- aggregates");
            sb.AppendLine("erDiagram");

            foreach (var entity in entities)
            {
                RenderEntity(sb, entity, includeFieldDetail);
            }

            if (relationships.Count > 0)
            {
                sb.AppendLine();
                foreach (var rel in relationships)
                {
                    RenderRelationship(sb, rel);
                }
            }

            return sb.ToString();
        }

        private static string RenderMasterDiagram(
            SortedDictionary<string, List<MermaidEntity>> bySlice,
            IReadOnlyList<MermaidRelationship> relationships)
        {
            var sb = new StringBuilder();
            sb.AppendLine("%% OASIS Sleek -- domain data model (master diagram)");
            sb.AppendLine("%% GENERATED FILE -- do not edit. Source: Persistence/SurrealDb/Schemas/source/*.mermaid");
            sb.AppendLine("%% Regenerate with: dotnet run --project packages/Oasis.SurrealDb.Schema -- aggregates");
            sb.AppendLine("%%");
            sb.AppendLine("%% Slices in this diagram:");
            foreach (var kvp in bySlice)
            {
                sb.Append("%%   ").Append(kvp.Key).Append(": ");
                sb.AppendLine(string.Join(", ", kvp.Value.Select(e => e.Name)));
            }
            sb.AppendLine("erDiagram");

            foreach (var kvp in bySlice)
            {
                sb.AppendLine();
                sb.AppendLine("    %% ── " + kvp.Key + " ───────────────");
                foreach (var entity in kvp.Value)
                {
                    RenderEntity(sb, entity, includeFieldDetail: false);
                }
            }

            if (relationships.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("    %% ── relationships ───────────────");
                foreach (var rel in relationships)
                {
                    RenderRelationship(sb, rel);
                }
            }

            return sb.ToString();
        }

        private static void RenderEntity(StringBuilder sb, MermaidEntity entity, bool includeFieldDetail)
        {
            sb.Append("    ").Append(entity.Name).AppendLine(" {");
            if (includeFieldDetail)
            {
                foreach (var attr in entity.Attributes)
                {
                    sb.Append("        ").Append(SafeType(attr.Type)).Append(' ').Append(attr.Name);
                    if (attr.IsKey)
                    {
                        sb.Append(" PK");
                    }
                    if (!string.IsNullOrEmpty(attr.Comment))
                    {
                        sb.Append(" \"").Append(attr.Comment).Append('\"');
                    }
                    sb.AppendLine();
                }
            }
            else
            {
                // Master diagram: collapse to a single id field so the
                // entity box renders without 40-line tables. The slice
                // file is where readers go for full field detail.
                sb.AppendLine("        string id PK");
            }
            sb.AppendLine("    }");
        }

        private static void RenderRelationship(StringBuilder sb, MermaidRelationship rel)
        {
            sb.Append("    ")
              .Append(rel.FromEntity)
              .Append(' ')
              .Append(rel.Cardinality)
              .Append(' ')
              .Append(rel.ToEntity);
            if (!string.IsNullOrEmpty(rel.Label))
            {
                sb.Append(" : \"").Append(rel.Label).Append('\"');
            }
            sb.AppendLine();
        }

        /// <summary>
        /// Mermaid erDiagram type tokens cannot contain angle brackets
        /// (<c>option&lt;string&gt;</c>) because the renderer parses them
        /// as syntax. Collapse to a pipe-free alias that keeps semantic intent.
        /// </summary>
        private static string SafeType(string type)
        {
            // option<X> -> "option_X"; array<X> -> "array_X"; map<K,V> -> "map_K_V"
            return type
                .Replace("<", "_")
                .Replace(">", "")
                .Replace(",", "_")
                .Replace(" ", "");
        }
    }
}
