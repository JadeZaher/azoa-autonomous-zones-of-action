// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest table.

#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
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

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of Quest.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Owner avatar (Guid('N') hex)")]
        [References(typeof(Avatar))]
        public string AvatarId { get; set; } = string.Empty;

        [FieldGroup("Caller-supplied label")]
        [Required(NotEmpty = true)]
        public string Name { get; set; } = string.Empty;

        [FieldGroup("Optional description")]
        public string? Description { get; set; }

        [FieldGroup("Source template id when instantiated from a QuestTemplate (null for hand-authored quests)")]
        [References(typeof(QuestTemplate), Optional = true)]
        public string? TemplateId { get; set; }

        [FieldGroup("Owning DappSeries when this quest is part of a composed dApp (null for standalone quests)")]
        [References(typeof(DappSeries), Optional = true)]
        public string? DappSeriesId { get; set; }

        [Column(Flexible = true)]
        [FieldGroup("Free-form caller-supplied metadata (string->string map)")]
        public JsonElement Metadata { get; set; }

        [FieldGroup("Definition birthdate -- STAYS on the definition, not a runtime artifact")]
        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }
    }
}
