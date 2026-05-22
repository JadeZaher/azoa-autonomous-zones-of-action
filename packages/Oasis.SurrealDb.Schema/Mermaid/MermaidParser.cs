// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.Schema -- Mermaid ER parser (Phase 4 task 19 + 20).
//
// One pass through the source file. Recognized grammar (subset of
// https://mermaid.js.org/syntax/entityRelationshipDiagram.html):
//
//   erDiagram
//       wallet {
//           string id PK "primary key"
//           string avatar_id
//           %% @surreal.assert "$value != NONE AND $value != \"\""
//           string chain_type
//           datetime created_date
//           %% @surreal.index unique fields=[avatar_id,chain_type,address] name=wallet_avatar_chain_address
//       }
//
//       wallet ||--o{ holon : owns
//
// Annotation DSL rules (task 20):
//   - Strict namespace -- only `@surreal.<known>` directives are accepted.
//     Unknown `@surreal.*` directives throw with file:line:col.
//   - Annotations associate with the *next* AST node (entity / attribute /
//     relationship). Multiple consecutive annotations stack onto the same
//     target.
//   - Entity-level directives: schemafull, option, live.
//   - Attribute-level directives: assert, option (nullable marker), index
//     (declared on an attribute but attaches to its parent entity).
//   - Relationship-level directives: relate.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Oasis.SurrealDb.Schema.Mermaid
{
    /// <summary>
    /// Parses a single .mermaid file into a <see cref="MermaidSchemaModel"/>.
    /// </summary>
    public static class MermaidParser
    {
        // Known @surreal.* directives. The parser is strict — anything not in
        // this set is a hard error (preserves namespacing across future
        // expansions without silently accepting drift-prone comments).
        private static readonly HashSet<string> KnownDirectives = new HashSet<string>(StringComparer.Ordinal)
        {
            // Core entity-level (plan.md task 20).
            "schemafull",
            "option",
            "live",
            // Core attribute-level + entity-level index.
            "assert",
            "index",
            // Core relationship-level.
            "relate",
            // Extended documentation/metadata directives (Phase 4 addition by A3 --
            // strict namespacing preserved: unknown @surreal.* still fails). These
            // carry the human-readable comments that the wave-1 .surql header
            // blocks already contained, so the generator can re-emit equivalent
            // self-documenting output without losing fidelity.
            "aggregate",   // entity header: aggregate (C# model reference)
            "guardrail",   // entity header: guardrail (G6 SCHEMAFULL, etc.)
            "note",        // entity-level free-form note (stacks)
            "default",     // attribute-level DEFAULT <value> (literal token after =)
            "section",     // entity-level subsection comment (e.g. "Indexes")
            "fieldgroup",  // attribute-level inline comment above this field
            // C# source-generator directives ([[surrealdb-schema-source-gen]]).
            // Compound `csharp.<sub>` directives are normalised by the parser
            // into the directive name `csharp` with `<sub>` collapsed into the
            // first argument. This keeps the KnownDirectives gate strict while
            // letting source-gen consumers continue to author `%% @surreal.csharp.property name=Foo`.
            "csharp",
        };

        // Sub-arg names that the compound-directive handler should keep as
        // explicit key=value pairs rather than promoting as positional values.
        // Used by `@surreal.csharp.property name=Foo` so the `name` key isn't
        // accidentally re-bound to the sub-directive value.
        private static readonly HashSet<string> KnownDirectiveSubArgNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "fields", "unique",
        };

        /// <summary>Parse from a file on disk.</summary>
        public static MermaidSchemaModel ParseFile(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var text = File.ReadAllText(path);
            return Parse(text, path);
        }

        /// <summary>Parse from an in-memory string.</summary>
        public static MermaidSchemaModel Parse(string source, string? fileName = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var ctx = new ParserContext(source, fileName ?? string.Empty);
            return ParseDocument(ctx);
        }

        // ──────────────────────────────────────────────────────────────────
        // Top-level: find `erDiagram` then parse body.
        // ──────────────────────────────────────────────────────────────────
        private static MermaidSchemaModel ParseDocument(ParserContext ctx)
        {
            ctx.SkipBlankAndPureComments(allowAnnotationCollection: false);
            if (!ctx.TryReadKeyword("erDiagram"))
            {
                throw new MermaidParseException(
                    ctx.File, ctx.Line, ctx.Column,
                    "expected 'erDiagram' header at start of document.");
            }
            ctx.ConsumeRestOfLine();

            var entities = new List<MermaidEntity>();
            var relationships = new List<MermaidRelationship>();
            var pendingAnnotations = new List<MermaidAnnotation>();

            while (!ctx.IsEnd)
            {
                // Drain annotation comments (they associate with the next node).
                if (ctx.TryCollectAnnotation(out var ann))
                {
                    pendingAnnotations.Add(ann);
                    continue;
                }

                if (ctx.SkipBlankOrPlainComment()) continue;

                // Lookahead for entity vs relationship.
                int savedPos = ctx.Position;
                int savedLine = ctx.Line;
                int savedCol = ctx.Column;

                var ident = ctx.TryReadIdentifier();
                if (ident == null)
                {
                    throw new MermaidParseException(
                        ctx.File, ctx.Line, ctx.Column,
                        "expected entity declaration or relationship; got unexpected token.");
                }

                ctx.SkipInlineSpaces();
                if (ctx.Peek() == '{')
                {
                    // Entity definition.
                    ctx.Advance(); // consume '{'
                    var entity = ParseEntityBody(ctx, ident, savedLine, pendingAnnotations);
                    entities.Add(entity);
                    pendingAnnotations = new List<MermaidAnnotation>();
                }
                else
                {
                    // Relationship: <fromIdent> <cardinality> <toIdent> [ : label ]
                    var cardinality = ctx.ReadCardinalityToken();
                    ctx.SkipInlineSpaces();
                    var toIdent = ctx.TryReadIdentifier()
                        ?? throw new MermaidParseException(
                            ctx.File, ctx.Line, ctx.Column,
                            "expected target entity name in relationship.");

                    string? label = null;
                    ctx.SkipInlineSpaces();
                    if (ctx.Peek() == ':')
                    {
                        ctx.Advance();
                        ctx.SkipInlineSpaces();
                        label = ctx.ReadLabelOrRestOfLine();
                    }
                    ctx.ConsumeRestOfLine();
                    relationships.Add(new MermaidRelationship(
                        ident, toIdent, cardinality, label, pendingAnnotations, savedLine));
                    pendingAnnotations = new List<MermaidAnnotation>();
                }
            }

            if (pendingAnnotations.Count > 0)
            {
                var orphan = pendingAnnotations[0];
                throw new MermaidParseException(
                    ctx.File, orphan.SourceLine, orphan.SourceColumn,
                    $"orphan @surreal.{orphan.Directive} annotation -- no following entity / attribute / relationship to attach to.");
            }

            return new MermaidSchemaModel(ctx.File, entities, relationships);
        }

        // ──────────────────────────────────────────────────────────────────
        // Entity body: { <attribute>* } with annotation handling.
        // ──────────────────────────────────────────────────────────────────
        private static MermaidEntity ParseEntityBody(
            ParserContext ctx,
            string entityName,
            int entityLine,
            List<MermaidAnnotation> entityAnnotations)
        {
            var attributes = new List<MermaidAttribute>();
            var indexes = new List<MermaidIndex>();
            var pendingAttrAnnotations = new List<MermaidAnnotation>();
            var entityAnnotationList = new List<MermaidAnnotation>(entityAnnotations);

            while (!ctx.IsEnd)
            {
                ctx.SkipInlineSpaces();
                if (ctx.Peek() == '\n' || ctx.Peek() == '\r')
                {
                    ctx.ConsumeRestOfLine();
                    continue;
                }
                if (ctx.Peek() == '}')
                {
                    // Flush any trailing annotations: `index` directives are
                    // entity-level so we accept them here. Other directives
                    // would be orphans (no attribute to attach to) and must
                    // fail rather than silently drop.
                    foreach (var pending in pendingAttrAnnotations)
                    {
                        if (pending.Directive == "index")
                        {
                            indexes.Add(MaterializeIndex(ctx, pending));
                        }
                        else
                        {
                            throw new MermaidParseException(
                                ctx.File, pending.SourceLine, pending.SourceColumn,
                                $"orphan @surreal.{pending.Directive} annotation at end of entity '{entityName}' -- no following attribute.");
                        }
                    }
                    pendingAttrAnnotations.Clear();
                    ctx.Advance();
                    ctx.ConsumeRestOfLine();
                    return new MermaidEntity(entityName, attributes, entityAnnotationList, indexes, entityLine);
                }

                if (ctx.TryCollectAnnotation(out var ann))
                {
                    pendingAttrAnnotations.Add(ann);
                    continue;
                }

                if (ctx.SkipBlankOrPlainComment()) continue;

                // Attribute line: <type> <name> [PK|FK|UK] ["comment"]
                int attrLine = ctx.Line;
                var typeTok = ctx.ReadAttributeType();
                ctx.SkipInlineSpaces();
                var nameTok = ctx.TryReadIdentifier()
                    ?? throw new MermaidParseException(
                        ctx.File, ctx.Line, ctx.Column,
                        $"expected attribute name after type '{typeTok}'.");

                bool isKey = false;
                ctx.SkipInlineSpaces();
                var keyStartSp = ctx.Save();
                var keyTok = ctx.TryReadIdentifier();
                if (keyTok != null && (keyTok == "PK" || keyTok == "FK" || keyTok == "UK"))
                {
                    isKey = true;
                }
                else if (keyTok != null)
                {
                    // Not a key flag -- rewind so the optional comment scan can claim it.
                    ctx.Restore(keyStartSp);
                }

                string? comment = null;
                ctx.SkipInlineSpaces();
                if (ctx.Peek() == '"')
                {
                    comment = ctx.ReadQuotedString();
                }
                ctx.ConsumeRestOfLine();

                // Decompose pending annotations: `index` belongs to entity; rest belong to attribute.
                var attrAnns = new List<MermaidAnnotation>();
                foreach (var a in pendingAttrAnnotations)
                {
                    if (a.Directive == "index")
                    {
                        indexes.Add(MaterializeIndex(ctx, a));
                    }
                    else
                    {
                        attrAnns.Add(a);
                    }
                }
                pendingAttrAnnotations = new List<MermaidAnnotation>();

                attributes.Add(new MermaidAttribute(nameTok, typeTok, isKey, comment, attrAnns, attrLine));
            }

            throw new MermaidParseException(
                ctx.File, ctx.Line, ctx.Column,
                $"unexpected end of input inside entity '{entityName}' (missing '}}').");
        }

        // ──────────────────────────────────────────────────────────────────
        // Annotation materialization helpers.
        // ──────────────────────────────────────────────────────────────────
        private static MermaidIndex MaterializeIndex(ParserContext ctx, MermaidAnnotation ann)
        {
            // Required arg: fields=[a,b,c]. Optional: unique, name=<id>.
            if (!ann.Arguments.TryGetValue("fields", out var fieldsRaw))
            {
                throw new MermaidParseException(
                    ctx.File, ann.SourceLine, ann.SourceColumn,
                    "@surreal.index requires fields=[a,b,...] argument.");
            }
            var fields = SplitFieldList(fieldsRaw)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            if (fields.Count == 0)
            {
                throw new MermaidParseException(
                    ctx.File, ann.SourceLine, ann.SourceColumn,
                    "@surreal.index fields=[...] cannot be empty.");
            }

            bool unique = ann.Arguments.ContainsKey("unique")
                || ann.RawArguments.IndexOf("unique", StringComparison.Ordinal) >= 0
                    && !ann.RawArguments.Contains("=unique");

            // The `unique` token may appear as a positional flag. Re-check.
            foreach (var tok in TokenizeAnnotationArgs(ann.RawArguments))
            {
                if (tok == "unique") { unique = true; break; }
            }

            if (!ann.Arguments.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            {
                throw new MermaidParseException(
                    ctx.File, ann.SourceLine, ann.SourceColumn,
                    "@surreal.index requires name=<identifier> argument.");
            }

            return new MermaidIndex(name, fields, unique, ann.SourceLine);
        }

        private static IEnumerable<string> SplitFieldList(string raw)
        {
            // raw is like "[a,b,c]" or "a,b,c". Strip surrounding brackets.
            var r = raw.Trim();
            if (r.Length >= 2 && r[0] == '[' && r[r.Length - 1] == ']')
            {
                r = r.Substring(1, r.Length - 2);
            }
            return r.Split(',');
        }

        private static IEnumerable<string> TokenizeAnnotationArgs(string raw)
        {
            // Splits on whitespace except when inside [ ] or " ".
            var sb = new StringBuilder();
            int brackets = 0;
            bool inQuote = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '"') inQuote = !inQuote;
                else if (!inQuote && c == '[') brackets++;
                else if (!inQuote && c == ']') brackets--;

                if (!inQuote && brackets == 0 && char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0)
                    {
                        yield return sb.ToString();
                        sb.Clear();
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        // ──────────────────────────────────────────────────────────────────
        // Inner parser context — owns the cursor + position tracking.
        // ──────────────────────────────────────────────────────────────────
        private sealed class ParserContext
        {
            public string Source { get; }
            public string File { get; }
            public int Position { get; private set; }
            public int Line { get; private set; }
            public int Column { get; private set; }
            public bool IsEnd => Position >= Source.Length;

            public ParserContext(string source, string file)
            {
                Source = source;
                File = file;
                Position = 0;
                Line = 1;
                Column = 1;
            }

            public char Peek() => IsEnd ? '\0' : Source[Position];

            public char PeekAt(int offset)
            {
                int p = Position + offset;
                return (p < 0 || p >= Source.Length) ? '\0' : Source[p];
            }

            public void Advance()
            {
                if (IsEnd) return;
                char c = Source[Position];
                Position++;
                if (c == '\n') { Line++; Column = 1; }
                else { Column++; }
            }

            // LOW #L3: O(N) per call, so chained-rewind hot paths inside the
            // parser were O(N^2) across the file. The Savepoint-based
            // Save/Restore variants below stash Line/Column alongside Position
            // so internal callers restore in constant time; this method is
            // kept as a compat shim for any external code that still calls it
            // by raw position (it always re-scans from 0 to recompute
            // Line/Column).
            public void Rewind(int targetPosition)
            {
                Line = 1; Column = 1;
                for (int i = 0; i < targetPosition; i++)
                {
                    if (Source[i] == '\n') { Line++; Column = 1; }
                    else { Column++; }
                }
                Position = targetPosition;
            }

            /// <summary>
            /// Capture a constant-time-restorable snapshot of the cursor
            /// (position + line + column). Pair with <see cref="Restore"/> to
            /// walk back over speculative reads without re-scanning the
            /// source from offset 0.
            /// </summary>
            public Savepoint Save() => new Savepoint(Position, Line, Column);

            /// <summary>
            /// Restore a previously captured <see cref="Savepoint"/> in O(1).
            /// </summary>
            public void Restore(Savepoint sp)
            {
                Position = sp.Position;
                Line = sp.Line;
                Column = sp.Column;
            }

            public readonly struct Savepoint
            {
                public Savepoint(int position, int line, int column)
                {
                    Position = position;
                    Line = line;
                    Column = column;
                }
                public int Position { get; }
                public int Line { get; }
                public int Column { get; }
            }

            public void SkipInlineSpaces()
            {
                while (!IsEnd)
                {
                    char c = Source[Position];
                    if (c == ' ' || c == '\t') Advance();
                    else break;
                }
            }

            public void ConsumeRestOfLine()
            {
                while (!IsEnd && Source[Position] != '\n') Advance();
                if (!IsEnd) Advance(); // consume the newline
            }

            /// <summary>Skip whitespace lines + non-annotation `%%` lines. Returns true if anything was skipped.</summary>
            public bool SkipBlankOrPlainComment()
            {
                int start = Position;
                while (!IsEnd)
                {
                    var saved = Save();
                    SkipInlineSpaces();
                    if (IsEnd) break;
                    char c = Source[Position];
                    if (c == '\n' || c == '\r') { Advance(); continue; }
                    if (c == '%' && PeekAt(1) == '%')
                    {
                        // Could be an annotation. Peek for the `@surreal.` marker.
                        if (LooksLikeAnnotation(saved.Position))
                        {
                            Restore(saved);
                            return Position != start;
                        }
                        ConsumeRestOfLine();
                        continue;
                    }
                    Restore(saved);
                    break;
                }
                return Position != start;
            }

            public void SkipBlankAndPureComments(bool allowAnnotationCollection)
            {
                while (!IsEnd)
                {
                    var saved = Save();
                    SkipInlineSpaces();
                    if (IsEnd) break;
                    char c = Source[Position];
                    if (c == '\n' || c == '\r') { Advance(); continue; }
                    if (c == '%' && PeekAt(1) == '%')
                    {
                        if (allowAnnotationCollection && LooksLikeAnnotation(saved.Position))
                        {
                            Restore(saved);
                            return;
                        }
                        ConsumeRestOfLine();
                        continue;
                    }
                    Restore(saved);
                    break;
                }
            }

            private bool LooksLikeAnnotation(int linePosition)
            {
                // Scan forward (without mutating cursor state) to see if the
                // first non-`%` non-space token starts with '@surreal.'.
                int p = linePosition;
                while (p < Source.Length && (Source[p] == ' ' || Source[p] == '\t')) p++;
                if (p + 1 >= Source.Length || Source[p] != '%' || Source[p + 1] != '%') return false;
                p += 2;
                while (p < Source.Length && (Source[p] == ' ' || Source[p] == '\t')) p++;
                if (p >= Source.Length || Source[p] != '@') return false;
                var rest = Source.Substring(p, Math.Min(Source.Length - p, 9));
                return rest.StartsWith("@surreal.", StringComparison.Ordinal);
            }

            public bool TryCollectAnnotation(out MermaidAnnotation annotation)
            {
                annotation = null!;
                var saved = Save();
                SkipInlineSpaces();
                if (IsEnd || Peek() != '%' || PeekAt(1) != '%') { Restore(saved); return false; }
                Advance(); Advance();
                SkipInlineSpaces();
                if (Peek() != '@') { Restore(saved); return false; }

                int markerLine = Line, markerCol = Column;
                // Read '@surreal.<directive>'
                if (!StartsWithAtPosition("@surreal."))
                {
                    Restore(saved);
                    return false;
                }
                Position += "@surreal.".Length;
                Column += "@surreal.".Length;

                var directiveSb = new StringBuilder();
                while (!IsEnd)
                {
                    char c = Source[Position];
                    if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                    {
                        directiveSb.Append(c);
                        Advance();
                    }
                    else break;
                }
                var directive = directiveSb.ToString();
                if (directive.Length == 0)
                {
                    throw new MermaidParseException(File, markerLine, markerCol,
                        "expected directive name after '@surreal.'.");
                }

                // Compound `@surreal.<directive>.<sub>` form: a single dot
                // immediately follows the directive name. Consume the sub-
                // identifier so it can be forwarded as a structured argument.
                // Strict gate: the sub-name must satisfy the same identifier
                // grammar as the directive; deeper nesting (more dots) is not
                // currently allowed and falls through to a normal parse error.
                string? subDirective = null;
                if (!IsEnd && Source[Position] == '.')
                {
                    Advance();
                    var subSb = new StringBuilder();
                    while (!IsEnd)
                    {
                        char c = Source[Position];
                        if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                        {
                            subSb.Append(c);
                            Advance();
                        }
                        else break;
                    }
                    subDirective = subSb.ToString();
                    if (subDirective.Length == 0)
                    {
                        throw new MermaidParseException(File, markerLine, markerCol,
                            $"expected sub-directive name after '@surreal.{directive}.'.");
                    }
                }

                if (!KnownDirectives.Contains(directive))
                {
                    throw new MermaidParseException(File, markerLine, markerCol,
                        $"unknown @surreal.{directive} directive; allowed: {string.Join(", ", KnownDirectives.OrderBy(d => d, StringComparer.Ordinal))}.");
                }

                SkipInlineSpaces();
                // Read remainder of line as raw arguments.
                int argStart = Position;
                while (!IsEnd && Source[Position] != '\n' && Source[Position] != '\r') Advance();
                var raw = Source.Substring(argStart, Position - argStart).TrimEnd();
                if (!IsEnd) Advance(); // consume newline

                var args = ParseAnnotationArgs(raw);

                // Inject the sub-directive as a structured argument so
                // downstream consumers (the source generator) can dispatch on
                // a single dictionary lookup instead of re-parsing the
                // directive string.
                //
                // Convention:
                //   `@surreal.csharp.skip`                  -> args["skip"] = ""
                //   `@surreal.csharp.property name=Foo`     -> args["property"] = "", args["name"] = "Foo"
                //   `@surreal.csharp.namespace OASIS.X`     -> args["namespace"] = "OASIS.X" (positional bare-token consumed as the sub-directive's value when no `=` form was supplied).
                if (subDirective != null)
                {
                    var augmented = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var kv in args) augmented[kv.Key] = kv.Value;
                    // Promote a bare positional token (the first arg with an
                    // empty value and a non-key name) to be the sub-directive's
                    // value. This lets `@surreal.csharp.namespace OASIS.X` map
                    // to args["namespace"] = "OASIS.X" without forcing the
                    // verbose `namespace=OASIS.X` form.
                    string? positionalToken = null;
                    foreach (var kv in args)
                    {
                        if (kv.Value.Length == 0 && kv.Key.Length > 0 && !KnownDirectiveSubArgNames.Contains(kv.Key))
                        {
                            positionalToken = kv.Key;
                            break;
                        }
                    }
                    if (positionalToken != null && !augmented.ContainsKey(subDirective))
                    {
                        augmented[subDirective] = positionalToken;
                        augmented.Remove(positionalToken);
                    }
                    else if (!augmented.ContainsKey(subDirective))
                    {
                        augmented[subDirective] = string.Empty;
                    }
                    args = augmented;
                }

                annotation = new MermaidAnnotation(directive, raw, args, markerLine, markerCol);
                return true;
            }

            private bool StartsWithAtPosition(string s)
            {
                if (Position + s.Length > Source.Length) return false;
                for (int i = 0; i < s.Length; i++)
                {
                    if (Source[Position + i] != s[i]) return false;
                }
                return true;
            }

            private static IReadOnlyDictionary<string, string> ParseAnnotationArgs(string raw)
            {
                // Recognizes:
                //   key=value
                //   key=[a,b,c]
                //   key="quoted with spaces"
                //   bare-token  (stored as key=bare-token; lookup by directive will check semantics)
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                int i = 0;
                while (i < raw.Length)
                {
                    while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
                    if (i >= raw.Length) break;

                    // Read key (until '=' or whitespace).
                    int keyStart = i;
                    while (i < raw.Length && !char.IsWhiteSpace(raw[i]) && raw[i] != '=') i++;
                    var key = raw.Substring(keyStart, i - keyStart);
                    string value = string.Empty;

                    if (i < raw.Length && raw[i] == '=')
                    {
                        i++; // skip '='
                        value = ReadValueToken(raw, ref i);
                    }
                    else
                    {
                        // Bare token: store under its own name with empty value.
                    }

                    if (key.Length > 0 && !dict.ContainsKey(key))
                    {
                        dict[key] = value;
                    }
                }
                return dict;
            }

            private static string ReadValueToken(string raw, ref int i)
            {
                if (i >= raw.Length) return string.Empty;
                char c = raw[i];
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++; // skip opening quote
                    while (i < raw.Length)
                    {
                        char ch = raw[i];
                        if (ch == '\\' && i + 1 < raw.Length)
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
                            i += 2;
                        }
                        else if (ch == '"')
                        {
                            i++;
                            return sb.ToString();
                        }
                        else { sb.Append(ch); i++; }
                    }
                    return sb.ToString();
                }
                if (c == '[')
                {
                    int depth = 0;
                    var sb = new StringBuilder();
                    while (i < raw.Length)
                    {
                        char ch = raw[i];
                        if (ch == '[') depth++;
                        else if (ch == ']')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                sb.Append(ch);
                                i++;
                                return sb.ToString();
                            }
                        }
                        sb.Append(ch);
                        i++;
                    }
                    return sb.ToString();
                }
                // Bare value: read up to whitespace.
                int start = i;
                while (i < raw.Length && !char.IsWhiteSpace(raw[i])) i++;
                return raw.Substring(start, i - start);
            }

            public bool TryReadKeyword(string kw)
            {
                var saved = Save();
                SkipInlineSpaces();
                if (Position + kw.Length > Source.Length || !StartsWithAtPosition(kw))
                {
                    Restore(saved);
                    return false;
                }
                // Ensure word boundary.
                char next = (Position + kw.Length < Source.Length) ? Source[Position + kw.Length] : '\0';
                if (char.IsLetterOrDigit(next) || next == '_')
                {
                    Restore(saved);
                    return false;
                }
                Position += kw.Length;
                Column += kw.Length;
                return true;
            }

            public string? TryReadIdentifier()
            {
                SkipInlineSpaces();
                if (IsEnd) return null;
                char c = Source[Position];
                if (!(char.IsLetter(c) || c == '_')) return null;
                var sb = new StringBuilder();
                while (!IsEnd)
                {
                    char cur = Source[Position];
                    if (char.IsLetterOrDigit(cur) || cur == '_')
                    {
                        sb.Append(cur);
                        Advance();
                    }
                    else break;
                }
                return sb.Length == 0 ? null : sb.ToString();
            }

            public string ReadAttributeType()
            {
                // Mermaid types are usually a single word but we tolerate
                // `option<string>` (angle-bracketed generics) for OASIS-flavored emit.
                SkipInlineSpaces();
                if (IsEnd)
                {
                    throw new MermaidParseException(File, Line, Column, "expected attribute type, got end of input.");
                }
                var sb = new StringBuilder();
                int depth = 0;
                while (!IsEnd)
                {
                    char c = Source[Position];
                    if (char.IsLetterOrDigit(c) || c == '_')
                    {
                        sb.Append(c);
                        Advance();
                    }
                    else if (c == '<') { sb.Append(c); depth++; Advance(); }
                    else if (c == '>') { sb.Append(c); depth--; Advance(); if (depth == 0) break; }
                    else if (depth > 0)
                    {
                        sb.Append(c);
                        Advance();
                    }
                    else break;
                }
                if (sb.Length == 0)
                {
                    throw new MermaidParseException(File, Line, Column, "expected attribute type, got unexpected token.");
                }
                return sb.ToString();
            }

            public string ReadCardinalityToken()
            {
                // Mermaid cardinality tokens like `||--o{`, `}o--o{`, `}|..|{`
                // contain letters (`o`) as well as punctuation. Boundary is
                // whitespace -- and conservatively, anything that starts a
                // plain identifier (letter / underscore) AFTER we've already
                // consumed at least one cardinality-shape character.
                SkipInlineSpaces();
                var sb = new StringBuilder();
                while (!IsEnd)
                {
                    char c = Source[Position];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') break;
                    sb.Append(c);
                    Advance();
                }
                if (sb.Length == 0)
                {
                    throw new MermaidParseException(File, Line, Column, "expected cardinality token in relationship.");
                }
                return sb.ToString();
            }

            public string ReadLabelOrRestOfLine()
            {
                SkipInlineSpaces();
                if (!IsEnd && Source[Position] == '"')
                {
                    return ReadQuotedString();
                }
                int start = Position;
                while (!IsEnd && Source[Position] != '\n' && Source[Position] != '\r') Advance();
                return Source.Substring(start, Position - start).TrimEnd();
            }

            public string ReadQuotedString()
            {
                if (Peek() != '"')
                {
                    throw new MermaidParseException(File, Line, Column, "expected opening quote.");
                }
                Advance();
                var sb = new StringBuilder();
                while (!IsEnd)
                {
                    char c = Source[Position];
                    if (c == '\\' && PeekAt(1) != '\0')
                    {
                        Advance();
                        char nx = Source[Position];
                        switch (nx)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            default: sb.Append(nx); break;
                        }
                        Advance();
                    }
                    else if (c == '"')
                    {
                        Advance();
                        return sb.ToString();
                    }
                    else if (c == '\n')
                    {
                        throw new MermaidParseException(File, Line, Column, "unterminated quoted string.");
                    }
                    else
                    {
                        sb.Append(c);
                        Advance();
                    }
                }
                throw new MermaidParseException(File, Line, Column, "unterminated quoted string at end of file.");
            }
        }
    }
}
