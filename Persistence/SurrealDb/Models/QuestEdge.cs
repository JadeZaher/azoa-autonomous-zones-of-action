// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest_edge table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
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
            /// <summary>Failure arm — target runs when source Failed, skipped when Succeeded.</summary>
            OnFailure,
        }

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of QuestEdge.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Owning quest")]
        [References(typeof(Quest))]
        public string QuestId { get; set; } = string.Empty;

        [FieldGroup("Upstream node")]
        [References(typeof(QuestNode))]
        public string SourceNodeId { get; set; } = string.Empty;

        [FieldGroup("Downstream node")]
        [References(typeof(QuestNode))]
        public string TargetNodeId { get; set; } = string.Empty;

        [FieldGroup("Optional condition expression (only with edge_type=Conditional)")]
        public string? Condition { get; set; }

        [FieldGroup("QuestEdgeType enum name")]
        [Inside("Control", "Conditional", "OnFailure")]
        [Default("\"Control\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public QuestEdgeTypeKind EdgeType { get; set; }
    }
}
