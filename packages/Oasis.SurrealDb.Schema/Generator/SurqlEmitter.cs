// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.Schema -- Deterministic .surql generator (Phase 4 task 21).
//
// MermaidSchemaModel  ->  SurqlEmitter.Emit  ->  string
//
// Determinism contract: same model input always produces the same byte
// sequence on output. No clock reads, no environment lookups, no
// HashSet enumeration order leakage. Entities, fields, indexes, and
// header lines are emitted in their original source order.
//
// Output shape (matches wave-1 stylistic conventions):
//
//   -- ============================================================
//   -- Table: <name>
//   -- Aggregate: <text>
//   -- Guardrail: <text>
//   -- Note: <text>
//   -- ============================================================
//
//   DEFINE TABLE <name> SCHEMAFULL;
//
//   DEFINE FIELD <name> ON TABLE <name> TYPE <type>
//       ASSERT <expr>
//       DEFAULT <value>;
//   ...
//
//   -- <section header>
//
//   DEFINE INDEX <idx_name>
//       ON TABLE <name>
//       FIELDS f1, f2
//       UNIQUE;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Oasis.SurrealDb.Schema.Mermaid;

namespace Oasis.SurrealDb.Schema.Generator
{
    /// <summary>
    /// Emits Surreal-QL DDL (`.surql`) from a parsed
    /// <see cref="MermaidSchemaModel"/>. Pure function — no I/O, no clocks.
    /// </summary>
    public static class SurqlEmitter
    {
        /// <summary>
        /// Map a numbered <c>NNN_name.mermaid</c> path to its sibling
        /// <c>NNN_name.surql</c> path (Phase 4 task 21 prefix preservation).
        /// </summary>
        public static string MapMermaidPathToSurql(string mermaidPath)
        {
            if (mermaidPath == null) throw new ArgumentNullException(nameof(mermaidPath));
            var dir = Path.GetDirectoryName(mermaidPath) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(mermaidPath);
            return string.IsNullOrEmpty(dir)
                ? name + ".surql"
                : Path.Combine(dir, name + ".surql");
        }

        /// <summary>
        /// Emit the model as a Surreal-QL string. Always uses Unix-style
        /// newlines and a single trailing newline for byte-stable output.
        /// </summary>
        public static string Emit(MermaidSchemaModel model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var sb = new StringBuilder();

            for (int i = 0; i < model.Entities.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                EmitEntity(sb, model.Entities[i]);
            }

            // Always exactly one trailing newline.
            if (sb.Length == 0 || sb[sb.Length - 1] != '\n') sb.Append('\n');
            return sb.ToString();
        }

        private static void EmitEntity(StringBuilder sb, MermaidEntity entity)
        {
            EmitEntityHeader(sb, entity);

            // DEFINE TABLE
            bool isSchemafull = HasDirective(entity.Annotations, "schemafull");
            sb.Append("DEFINE TABLE ").Append(entity.Name);
            if (isSchemafull) sb.Append(" SCHEMAFULL");
            sb.Append(";\n\n");

            // Fields
            string? lastFieldGroup = null;
            for (int i = 0; i < entity.Attributes.Count; i++)
            {
                var attr = entity.Attributes[i];

                // Field-group separator (renders as `-- <text>` comment line).
                var group = FirstArg(attr.Annotations, "fieldgroup");
                if (group != null && group != lastFieldGroup)
                {
                    if (i > 0) sb.Append('\n');
                    sb.Append("-- ").Append(group).Append('\n');
                    lastFieldGroup = group;
                }

                EmitField(sb, entity.Name, attr);
            }

            // Indexes
            if (entity.Indexes.Count > 0)
            {
                sb.Append('\n');
                var sectionHeader = FirstArg(entity.Annotations, "section") ?? DefaultIndexSection();
                sb.Append("-- ").Append(sectionHeader).Append('\n');
                sb.Append('\n');
                for (int i = 0; i < entity.Indexes.Count; i++)
                {
                    if (i > 0) sb.Append('\n');
                    EmitIndex(sb, entity.Name, entity.Indexes[i]);
                }
            }
        }

        private static string DefaultIndexSection()
            => "── Indexes ──────────────────────────────────────────────────";

        private static void EmitEntityHeader(StringBuilder sb, MermaidEntity entity)
        {
            sb.Append("-- ============================================================\n");
            sb.Append("-- Table: ").Append(entity.Name).Append('\n');

            var aggregate = FirstArg(entity.Annotations, "aggregate");
            if (!string.IsNullOrEmpty(aggregate))
            {
                sb.Append("-- Aggregate: ").Append(aggregate).Append('\n');
            }
            var guardrail = FirstArg(entity.Annotations, "guardrail");
            if (!string.IsNullOrEmpty(guardrail))
            {
                sb.Append("-- Guardrail: ").Append(guardrail).Append('\n');
            }
            foreach (var note in AllArgs(entity.Annotations, "note"))
            {
                // Allow embedded `\n` so a single note directive can carry a
                // multi-line block (each line gets its own `-- ` prefix).
                foreach (var line in note.Split('\n'))
                {
                    sb.Append("-- Note: ").Append(line).Append('\n');
                }
            }
            sb.Append("-- ============================================================\n");
            sb.Append('\n');
        }

        private static void EmitField(StringBuilder sb, string table, MermaidAttribute attr)
        {
            // Type: preserved verbatim from .mermaid (so `option<string>` flows through).
            sb.Append("DEFINE FIELD ").Append(attr.Name)
              .Append(" ON TABLE ").Append(table)
              .Append(" TYPE ").Append(attr.Type);

            // ASSERT clause (from @surreal.assert "<expr>").
            var assertExpr = FirstArg(attr.Annotations, "assert");
            if (!string.IsNullOrEmpty(assertExpr))
            {
                sb.Append('\n').Append("    ASSERT ").Append(assertExpr);
            }

            // DEFAULT clause (from @surreal.default "<value>").
            var defaultValue = FirstArg(attr.Annotations, "default");
            if (!string.IsNullOrEmpty(defaultValue))
            {
                sb.Append('\n').Append("    DEFAULT ").Append(defaultValue);
            }

            sb.Append(";\n");
        }

        private static void EmitIndex(StringBuilder sb, string table, MermaidIndex idx)
        {
            sb.Append("DEFINE INDEX ").Append(idx.Name).Append('\n');
            sb.Append("    ON TABLE ").Append(table).Append('\n');
            sb.Append("    FIELDS ").Append(string.Join(", ", idx.Fields));
            if (idx.IsUnique) sb.Append('\n').Append("    UNIQUE");
            sb.Append(";\n");
        }

        // ── helpers ───────────────────────────────────────────────────────
        private static bool HasDirective(IReadOnlyList<MermaidAnnotation> anns, string directive)
        {
            foreach (var a in anns) if (a.Directive == directive) return true;
            return false;
        }

        /// <summary>
        /// Returns the first positional / quoted argument of the first
        /// annotation matching <paramref name="directive"/>, or null if not present.
        /// Bare-token directives (e.g. <c>@surreal.aggregate Wallet</c>) return the
        /// raw arguments string; quoted directives return the unescaped value.
        /// </summary>
        private static string? FirstArg(IReadOnlyList<MermaidAnnotation> anns, string directive)
        {
            foreach (var a in anns)
            {
                if (a.Directive == directive) return ExtractPrimaryValue(a);
            }
            return null;
        }

        private static IEnumerable<string> AllArgs(IReadOnlyList<MermaidAnnotation> anns, string directive)
        {
            foreach (var a in anns)
            {
                if (a.Directive == directive)
                {
                    var v = ExtractPrimaryValue(a);
                    if (v != null) yield return v;
                }
            }
        }

        private static string? ExtractPrimaryValue(MermaidAnnotation a)
        {
            // If the arguments dict has a single key with an empty value, that
            // key is the positional bare-token value (e.g. `@surreal.schemafull`).
            // Otherwise pick the first key=value pair value.
            // For @surreal.<dir> "literal text", we wrap the literal in the raw
            // args; reinterpret it here.
            var raw = a.RawArguments?.Trim() ?? string.Empty;
            if (raw.Length == 0) return string.Empty;
            // Quoted-string special case: if the whole raw payload is a single
            // quoted string, return its unescaped body.
            if (raw[0] == '"')
            {
                var (decoded, consumed) = TryDecodeQuoted(raw);
                if (consumed == raw.Length) return decoded;
            }
            return raw;
        }

        private static (string decoded, int consumed) TryDecodeQuoted(string raw)
        {
            if (raw.Length == 0 || raw[0] != '"') return (raw, 0);
            var sb = new StringBuilder();
            for (int i = 1; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\\' && i + 1 < raw.Length)
                {
                    char nx = raw[i + 1];
                    switch (nx)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(nx); break;
                    }
                    i++; // skip the escape char
                }
                else if (c == '"')
                {
                    return (sb.ToString(), i + 1);
                }
                else sb.Append(c);
            }
            return (sb.ToString(), raw.Length);
        }
    }
}
