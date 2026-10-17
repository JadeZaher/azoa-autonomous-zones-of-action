// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the action_template table.

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("action_template",
        Aggregate = "ActionTemplate (Models/Actions/ActionTemplate.cs)",
        Guardrail = "G6 SCHEMAFULL; read-only catalog data populated by template authors")]
    [SurrealNote("Definition-side action catalog. An ActionTemplate is a reusable DAG blueprint instantiated by ActionInstantiator (Services/Actions/ActionInstantiator.cs) into a runtime action with concrete parameters. The runtime action_run + action_node_execution tables live in [[action-temporal-fork-model]] (see SURREAL-SCHEMA-HINTS.md); this table is the parameterised definition the instantiator reads.")]
    [SurrealNote("Nodes + edges embed as FLEXIBLE array<object> fields on the parent template row. They are NEVER queried independently of the template -- the instantiator reads the whole template in one record-id SELECT, walks the embedded slots, then resolves each NodeTemplateId via action_node_template (table 140). Embedding keeps the read O(1) per template; separating into child tables would force a join that has no other consumer.")]
    [SurrealNote("parameters is a JSON-Schema-encoded string declaring required parameters for instantiation (e.g. {\"required\":[\"holonId\"]}). version + tags + is_public are surfaced by the future template-marketplace UI.")]
    [Slice("action_templates")]
    [Index("action_template_by_author", Fields = new[] { "author_user_id" })]
    [ExtraSurrealField("nodes", "array<object>", Order = 8, Flexible = true,
        FieldGroup = "Embedded template-node slots (FLEXIBLE -- shape validated by C# POCO at deserialization time, not by SurrealDB).")]
    [ExtraSurrealField("edges", "array<object>", Order = 9, Flexible = true,
        FieldGroup = "Embedded template-edge slots (FLEXIBLE -- same rationale as nodes).")]
    public partial class ActionTemplate : ISurrealRecord
    {
        public const string SchemaNameConst = "action_template";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [FieldGroup("Core identity (record id is the Guid('N') of ActionTemplate.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [FieldGroup("Caller-supplied label")]
        public string Name { get; set; } = string.Empty;

        [Column(Order = 3)]
        [FieldGroup("Optional description")]
        public string? Description { get; set; }

        [Column(Order = 4)]
        [FieldGroup("Owner user (Guid('N') hex)")]
        [Required(NotEmpty = true)]
        public string AuthorUserId { get; set; } = string.Empty;

        [Column(Order = 5)]
        [FieldGroup("JSON-Schema for instantiation parameters (string-encoded)")]
        public string Parameters { get; set; } = string.Empty;

        [Column(Order = 6)]
        [FieldGroup("Semantic version (free string -- e.g. 1.0.0)")]
        public string Version { get; set; } = string.Empty;

        [Column(Order = 7)]
        [FieldGroup("Marketplace visibility flag")]
        [Default("false")]
        public bool IsPublic { get; set; }

        // Order = 8 is the [ExtraSurrealField("nodes", ...)] declared at class level.
        // Order = 9 is the [ExtraSurrealField("edges", ...)] declared at class level.

        [Column(Order = 10)]
        [FieldGroup("Free-form tags.")]
        public IReadOnlyList<string> Tags { get; set; } = System.Array.Empty<string>();
    }
}
