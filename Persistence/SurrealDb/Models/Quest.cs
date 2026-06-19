// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest table.

#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("quest",
        Aggregate = "Quest (Models/Quest/Quest.cs)",
        Guardrail = "G6 SCHEMAFULL; definition-side workflow shape. Runtime state lives on quest_run + quest_node_execution per quest-temporal-fork-model ADR §1.")]
    [SurrealNote("Shape-only definition: name, owner, template ancestry, dapp-series binding, free-form metadata. status/completed_date were intentionally removed when quest-temporal-fork-model split runtime from definition -- see SURREAL-SCHEMA-HINTS.md §1 'Removed from quest'.")]
    [SurrealNote("nodes/edges/dependencies live in their own tables (quest_node, quest_edge, quest_dependency) keyed by quest_id -- they are queried independently of the parent and mutated through Add/Remove endpoints. Embedding them would force the controller-side node/edge CRUD endpoints to read-modify-write the entire quest row.")]
    [SurrealNote("metadata is a free-form string->string map for caller-supplied tags (e.g. UI color, sort hint). Persisted as a SurrealDB object to keep arbitrary keys queryable.")]
    [Slice("quest")]
    [ExtraSurrealField("embedding", "option<array<float, 384>>", Order = 9, FieldGroup = "Embedding vector for HNSW semantic search (384-dimensional, MiniLM-style)")]
    [Index("quest_by_avatar", Fields = new[] { "avatar_id" })]
    [Index("quest_by_template", Fields = new[] { "template_id" })]
    [Index("quest_by_dapp_series", Fields = new[] { "dapp_series_id" })]
    public partial class Quest : ISurrealRecord
    {
        public const string SchemaNameConst = "quest";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1, Type = "string")]
        [FieldGroup("Core identity (record id is the Guid('N') of Quest.Id)")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [FieldGroup("Owner avatar (Guid('N') hex)")]
        [References(typeof(Avatar))]
        [JsonPropertyName("avatar_id")]
        public string AvatarId { get; set; } = string.Empty;

        [Column(Order = 3, Type = "string")]
        [FieldGroup("Caller-supplied label")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [Column(Order = 4, Type = "option<string>")]
        [FieldGroup("Optional description")]
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [Column(Order = 5)]
        [FieldGroup("Source template id when instantiated from a QuestTemplate (null for hand-authored quests)")]
        [References(typeof(QuestTemplate), Optional = true)]
        [JsonPropertyName("template_id")]
        public string? TemplateId { get; set; }

        [Column(Order = 6)]
        [FieldGroup("Owning DappSeries when this quest is part of a composed dApp (null for standalone quests)")]
        [References(typeof(DappSeries), Optional = true)]
        [JsonPropertyName("dapp_series_id")]
        public string? DappSeriesId { get; set; }

        [Column(Order = 7, Type = "object", Flexible = true)]
        [FieldGroup("Free-form caller-supplied metadata (string->string map)")]
        [JsonPropertyName("metadata")]
        public JsonElement Metadata { get; set; }

        [Column(Order = 8, Type = "datetime")]
        [FieldGroup("Definition birthdate -- STAYS on the definition, not a runtime artifact")]
        [ReadOnly]
        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }
    }
}
