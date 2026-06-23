// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest_dependency table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("quest_dependency",
        Aggregate = "QuestDependency (Models/Quest/QuestDependency.cs)",
        Guardrail = "G6 SCHEMAFULL; cross-quest dependency edge. Quest activation refuses to enter Active until all Required dependencies are satisfied.")]
    [SurrealNote("Optional vs Required is a hint to the dependency-check endpoint -- Required dependencies block activation; Optional ones are reported in DependencyCheckResult for caller visibility but do not block.")]
    [SurrealNote("depends_on_node_id is set when the dependency is on a specific node's output (e.g. 'must have HolonCreate's holonId') rather than the entire quest's completion. Null = depend on the whole quest reaching Succeeded.")]
    [Slice("quest")]
    [Index("quest_dependency_by_quest", Fields = new[] { "quest_id" })]
    [Index("quest_dependency_by_target", Fields = new[] { "depends_on_quest_id" })]
    public partial class QuestDependency : ISurrealRecord
    {
        public const string SchemaNameConst = "quest_dependency";
        public string SchemaName => SchemaNameConst;

        public enum QuestDependencyTypeKind
        {
            Required,
            Optional,
        }

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of QuestDependency.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Dependent quest (the one that won't run until the dependency is satisfied)")]
        [References(typeof(Quest))]
        public string QuestId { get; set; } = string.Empty;

        [FieldGroup("Dependency target quest")]
        [References(typeof(Quest))]
        public string DependsOnQuestId { get; set; } = string.Empty;

        [FieldGroup("Optional: depend on a specific node output rather than full quest completion")]
        [References(typeof(QuestNode), Optional = true)]
        public string? DependsOnNodeId { get; set; }

        [FieldGroup("QuestDependencyType enum name")]
        [Inside("Required", "Optional")]
        [Default("\"Required\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public QuestDependencyTypeKind DependencyType { get; set; }
    }
}
