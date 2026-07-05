// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the dapp_series_quest table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("dapp_series_quest",
        Aggregate = "DappSeriesQuest (dapp-composition track -- new entity, no hand-written model)",
        Guardrail = "G6 SCHEMAFULL; greenfield entity introduced by the dapp-composition track. Carries the ordering and cross-quest data flow within a DappSeries.")]
    [SurrealNote("An ordered quest entry inside a DappSeries. Composite key concept: (dapp_series_id, quest_id) is logically unique but stored as a real row to allow soft-update of order and input_mappings without delete+reinsert.")]
    [SurrealNote("order is 1-indexed and dense: ReorderQuestAsync resequences sibling rows transactionally. input_mappings is a JSON array describing how outputs from prior quests' terminal nodes flow into this quest's entry nodes (see DappSeries spec §InputMapping for the shape: sourceQuestId/sourceNodeId/targetQuestId/targetNodeId/fieldMap).")]
    [Slice("dapp_composition")]
    [Index("dapp_series_quest_by_series", Fields = new[] { "dapp_series_id" })]
    [Index("dapp_series_quest_by_order", Fields = new[] { "dapp_series_id", "order" })]
    [Index("dapp_series_quest_by_quest", Fields = new[] { "quest_id" })]
    public partial class DappSeriesQuest : ISurrealRecord
    {
        public const string SchemaNameConst = "dapp_series_quest";
        public string SchemaName => SchemaNameConst;

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of DappSeriesQuest.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Parent series")]
        [References(typeof(DappSeries))]
        public string DappSeriesId { get; set; } = string.Empty;

        [FieldGroup("Referenced quest")]
        [References(typeof(Quest))]
        public string QuestId { get; set; } = string.Empty;

        [FieldGroup("1-indexed execution order within the series")]
        [Assert("$value > 0")]
        public long Order { get; set; }

        [FieldGroup("JSON array of InputMapping entries (null when no cross-quest flow needed)")]
        public string? InputMappings { get; set; }
    }
}
