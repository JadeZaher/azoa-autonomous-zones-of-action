// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest_edge table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("quest_edge",
        Aggregate = "QuestEdge (Models/Quest/QuestEdge.cs)",
        Guardrail = "G6 SCHEMAFULL; definition-side directed control-flow edge between two quest_node rows in the same quest.")]
    [SurrealNote("Scalar edge table is the conservative shape; the surrealdb-migration track may promote this to a native `RELATE source_node -> control_flow -> target_node` edge for cheaper traversal. Both shapes preserve intra-iteration DAG semantics; the scalar form keeps QuestDagValidator queries unchanged.")]
    [SurrealNote("condition is populated only when edge_type = Conditional; the ConditionNodeHandler evaluates the expression against the upstream node's serialized output to gate edge activation.")]
    [Slice("quest")]
    [Index("quest_edge_by_quest", Fields = new[] { "quest_id" })]
    [Index("quest_edge_by_source", Fields = new[] { "source_node_id" })]
    [Index("quest_edge_by_target", Fields = new[] { "target_node_id" })]
    public partial class QuestEdge : ISurrealRecord
    {
        public const string SchemaNameConst = "quest_edge";
        public string SchemaName => SchemaNameConst;

        public enum QuestEdgeTypeKind
        {
            Control,
            Conditional,
        }

        [Id, Column(Order = 1, Type = "string")]
        [FieldGroup("Core identity (record id is the Guid('N') of QuestEdge.Id)")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [FieldGroup("Owning quest")]
        [References(typeof(Quest))]
        [JsonPropertyName("quest_id")]
        public string QuestId { get; set; } = string.Empty;

        [Column(Order = 3)]
        [FieldGroup("Upstream node")]
        [References(typeof(QuestNode))]
        [JsonPropertyName("source_node_id")]
        public string SourceNodeId { get; set; } = string.Empty;

        [Column(Order = 4)]
        [FieldGroup("Downstream node")]
        [References(typeof(QuestNode))]
        [JsonPropertyName("target_node_id")]
        public string TargetNodeId { get; set; } = string.Empty;

        [Column(Order = 5, Type = "option<string>")]
        [FieldGroup("Optional condition expression (only with edge_type=Conditional)")]
        [JsonPropertyName("condition")]
        public string? Condition { get; set; }

        [Column(Order = 6, Type = "string")]
        [FieldGroup("QuestEdgeType enum name")]
        [Inside("Control", "Conditional")]
        [Default("\"Control\"")]
        [JsonPropertyName("edge_type"), JsonConverter(typeof(JsonStringEnumConverter))]
        public QuestEdgeTypeKind EdgeType { get; set; }
    }
}
