// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.Schema -- Mermaid ER schema model.
//
// MermaidSchemaModel is the immutable AST produced by MermaidParser and
// consumed by SurqlEmitter. Phase-4 task 19 (mermaid parser) and task 21
// (generator) hinge on this shape.
//
// Design notes:
//   - Attributes carry both Mermaid-native types ("string", "datetime",
//     "option<string>", "int", "bool", "decimal", "object") and OASIS
//     annotations parsed from `%% @surreal.*` comment lines that immediately
//     precede the attribute / entity / index in the source file.
//   - The model is deliberately small: aggregate ER constructs into a single
//     pass that yields exactly enough information to emit Wave-1-shaped .surql.
//   - Source positions (`SourceLine`) are preserved on every node so the
//     annotation-DSL parser can report errors at the original line:col.

using System.Collections.Generic;

namespace Oasis.SurrealDb.Schema.Mermaid
{
    /// <summary>
    /// Root document: zero-or-more entities and zero-or-more relationships
    /// extracted from a single <c>erDiagram</c> block.
    /// </summary>
    public sealed class MermaidSchemaModel
    {
        /// <summary>
        /// The source file the model was parsed from. Used to construct
        /// <c>file:line:col</c> error messages downstream.
        /// </summary>
        public string SourceFile { get; }

        /// <summary>Entities in source order (preserved for deterministic emit).</summary>
        public IReadOnlyList<MermaidEntity> Entities { get; }

        /// <summary>Relationships in source order.</summary>
        public IReadOnlyList<MermaidRelationship> Relationships { get; }

        public MermaidSchemaModel(
            string sourceFile,
            IReadOnlyList<MermaidEntity> entities,
            IReadOnlyList<MermaidRelationship> relationships)
        {
            SourceFile = sourceFile ?? string.Empty;
            Entities = entities ?? new List<MermaidEntity>();
            Relationships = relationships ?? new List<MermaidRelationship>();
        }
    }

    /// <summary>
    /// One ER entity (= one <c>DEFINE TABLE</c> on emit).
    /// </summary>
    public sealed class MermaidEntity
    {
        /// <summary>Entity name (Mermaid identifier).</summary>
        public string Name { get; }

        /// <summary>Attributes in source order.</summary>
        public IReadOnlyList<MermaidAttribute> Attributes { get; }

        /// <summary>Annotations attached to the entity (e.g. <c>@surreal.schemafull</c>).</summary>
        public IReadOnlyList<MermaidAnnotation> Annotations { get; }

        /// <summary>Indexes declared on this entity (from <c>@surreal.index</c>).</summary>
        public IReadOnlyList<MermaidIndex> Indexes { get; }

        /// <summary>1-based source line where the entity name appears.</summary>
        public int SourceLine { get; }

        public MermaidEntity(
            string name,
            IReadOnlyList<MermaidAttribute> attributes,
            IReadOnlyList<MermaidAnnotation> annotations,
            IReadOnlyList<MermaidIndex> indexes,
            int sourceLine)
        {
            Name = name;
            Attributes = attributes ?? new List<MermaidAttribute>();
            Annotations = annotations ?? new List<MermaidAnnotation>();
            Indexes = indexes ?? new List<MermaidIndex>();
            SourceLine = sourceLine;
        }
    }

    /// <summary>
    /// One ER attribute (= one <c>DEFINE FIELD</c> on emit).
    /// </summary>
    public sealed class MermaidAttribute
    {
        /// <summary>Attribute name (Mermaid identifier).</summary>
        public string Name { get; }

        /// <summary>
        /// Raw Mermaid type token. Preserved verbatim; the emitter maps to
        /// Surreal type syntax (<c>option&lt;string&gt;</c> stays as-is).
        /// </summary>
        public string Type { get; }

        /// <summary>True if attribute carries Mermaid PK / FK / UK marker.</summary>
        public bool IsKey { get; }

        /// <summary>
        /// The trailing <c>"comment"</c> token from Mermaid syntax (e.g.
        /// <c>string id PK "primary key"</c>). May be null.
        /// </summary>
        public string? Comment { get; }

        /// <summary>Annotations attached to this attribute.</summary>
        public IReadOnlyList<MermaidAnnotation> Annotations { get; }

        /// <summary>1-based source line.</summary>
        public int SourceLine { get; }

        public MermaidAttribute(
            string name,
            string type,
            bool isKey,
            string? comment,
            IReadOnlyList<MermaidAnnotation> annotations,
            int sourceLine)
        {
            Name = name;
            Type = type;
            IsKey = isKey;
            Comment = comment;
            Annotations = annotations ?? new List<MermaidAnnotation>();
            SourceLine = sourceLine;
        }
    }

    /// <summary>
    /// An ER relationship line (e.g. <c>wallet ||--o{ holon : owns</c>).
    /// </summary>
    public sealed class MermaidRelationship
    {
        public string FromEntity { get; }
        public string ToEntity { get; }
        public string Cardinality { get; }
        public string? Label { get; }
        public IReadOnlyList<MermaidAnnotation> Annotations { get; }
        public int SourceLine { get; }

        public MermaidRelationship(
            string fromEntity,
            string toEntity,
            string cardinality,
            string? label,
            IReadOnlyList<MermaidAnnotation> annotations,
            int sourceLine)
        {
            FromEntity = fromEntity;
            ToEntity = toEntity;
            Cardinality = cardinality;
            Label = label;
            Annotations = annotations ?? new List<MermaidAnnotation>();
            SourceLine = sourceLine;
        }
    }

    /// <summary>
    /// A parsed <c>%% @surreal.&lt;directive&gt;</c> line. Strict namespacing
    /// — anything not in the known directive set fails the parser.
    /// </summary>
    public sealed class MermaidAnnotation
    {
        /// <summary>
        /// Directive name without the <c>@surreal.</c> prefix
        /// (<c>schemafull</c>, <c>assert</c>, <c>option</c>, <c>index</c>,
        /// <c>relate</c>, <c>live</c>).
        /// </summary>
        public string Directive { get; }

        /// <summary>The raw argument text following the directive name (may be empty).</summary>
        public string RawArguments { get; }

        /// <summary>Parsed key=value / positional args (best-effort).</summary>
        public IReadOnlyDictionary<string, string> Arguments { get; }

        /// <summary>1-based source line of the <c>%%</c> comment.</summary>
        public int SourceLine { get; }

        /// <summary>1-based source column.</summary>
        public int SourceColumn { get; }

        public MermaidAnnotation(
            string directive,
            string rawArguments,
            IReadOnlyDictionary<string, string> arguments,
            int sourceLine,
            int sourceColumn)
        {
            Directive = directive;
            RawArguments = rawArguments ?? string.Empty;
            Arguments = arguments ?? new Dictionary<string, string>();
            SourceLine = sourceLine;
            SourceColumn = sourceColumn;
        }
    }

    /// <summary>
    /// Materialized index declaration (from <c>@surreal.index</c>).
    /// One <c>DEFINE INDEX</c> per instance.
    /// </summary>
    public sealed class MermaidIndex
    {
        public string Name { get; }
        public IReadOnlyList<string> Fields { get; }
        public bool IsUnique { get; }
        public int SourceLine { get; }

        public MermaidIndex(string name, IReadOnlyList<string> fields, bool isUnique, int sourceLine)
        {
            Name = name;
            Fields = fields;
            IsUnique = isUnique;
            SourceLine = sourceLine;
        }
    }
}
