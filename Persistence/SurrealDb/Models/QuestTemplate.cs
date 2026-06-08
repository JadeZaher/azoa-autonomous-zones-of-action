// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest_template table.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("quest_template",
        Aggregate = "QuestTemplate (Models/Quest/QuestTemplate.cs)",
        Guardrail = "G6 SCHEMAFULL; read-only catalog data populated by template authors")]
    [SurrealNote("Definition-side quest catalog. A QuestTemplate is a reusable DAG blueprint instantiated by QuestInstantiator (Services/Quest/QuestInstantiator.cs) into a runtime Quest with concrete parameters. The runtime quest_run + quest_node_execution tables live in [[quest-temporal-fork-model]] (see SURREAL-SCHEMA-HINTS.md); this table is the parameterised definition the instantiator reads.")]
    [SurrealNote("Nodes + edges embed as FLEXIBLE array<object> fields on the parent template row. They are NEVER queried independently of the template -- the instantiator reads the whole template in one record-id SELECT, walks the embedded slots, then resolves each NodeTemplateId via quest_node_template (table 140). Embedding keeps the read O(1) per template; separating into child tables would force a join that has no other consumer.")]
    [SurrealNote("parameters is a JSON-Schema-encoded string declaring required parameters for instantiation (e.g. {\"required\":[\"holonId\"]}). version + tags + is_public are surfaced by the future template-marketplace UI.")]
    [Slice("quest_templates")]
    [Index("quest_template_by_author", Fields = new[] { "author_avatar_id" })]
    [ExtraSurrealField("nodes", "array<object>", Order = 8, Flexible = true,
        FieldGroup = "Embedded template-node slots (FLEXIBLE -- shape validated by C# POCO at deserialization time, not by SurrealDB).")]
    [ExtraSurrealField("edges", "array<object>", Order = 9, Flexible = true,
        FieldGroup = "Embedded template-edge slots (FLEXIBLE -- same rationale as nodes).")]
    public partial class QuestTemplate : ISurrealRecord
    {
        public const string SchemaNameConst = "quest_template";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1, Type = "string")]
        [FieldGroup("Core identity (record id is the Guid('N') of QuestTemplate.Id)")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2, Type = "string")]
        [FieldGroup("Caller-supplied label")]
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [Column(Order = 3, Type = "option<string>")]
        [FieldGroup("Optional description")]
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [Column(Order = 4, Type = "string")]
        [FieldGroup("Owner avatar (Guid('N') hex)")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("author_avatar_id")]
        public string AuthorAvatarId { get; set; } = string.Empty;

        [Column(Order = 5, Type = "string")]
        [FieldGroup("JSON-Schema for instantiation parameters (string-encoded)")]
        [JsonPropertyName("parameters")]
        public string Parameters { get; set; } = string.Empty;

        [Column(Order = 6, Type = "string")]
        [FieldGroup("Semantic version (free string -- e.g. 1.0.0)")]
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [Column(Order = 7, Type = "bool")]
        [FieldGroup("Marketplace visibility flag")]
        [Default("false")]
        [JsonPropertyName("is_public")]
        public bool IsPublic { get; set; }

        // Order = 8 is the [ExtraSurrealField("nodes", ...)] declared at class level.
        // Order = 9 is the [ExtraSurrealField("edges", ...)] declared at class level.

        [Column(Order = 10, Type = "array<object>", Flexible = true)]
        [FieldGroup("Free-form tags.")]
        [JsonPropertyName("tags")]
        public IReadOnlyList<string> Tags { get; set; } = System.Array.Empty<string>();
    }
}
