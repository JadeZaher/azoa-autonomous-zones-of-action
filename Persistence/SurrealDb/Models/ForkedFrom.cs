// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the forked_from RELATE-edge table.

#nullable enable

using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("forked_from",
        Aggregate = "Quest graph RELATE edges (quest-temporal-fork-model + surrealdb-migration §6)",
        Guardrail = "G6 SCHEMAFULL; native RELATION tables for cheap graph traversal of fork lineage (forked_from) and copy-by-reference per-(run, node) ownership (executes). No POCO emission expected -- these are pure RELATE-edge tables consumed via -> traversal in SurrealQuery.")]
    [SurrealNote("forked_from is many-to-one (quest_run -> quest_run). Mirrors the scalar parent_run_id on quest_run; both must be kept in sync at write time. Enables `->forked_from->...` multi-hop ancestor walks without scalar joins. See SURREAL-SCHEMA-HINTS.md §6.1.")]
    [SurrealNote("executes is many-to-many (quest_run -> quest_node_execution). When a node execution is first created for a run, RELATE run->executes->exec is created alongside the CREATE. When a fork happens, for every quest_node_execution where node_id.execution_order < forkPoint, an additional RELATE child_run->executes->exec is added. The exec row is NOT duplicated -- the relationship is many-to-many. See SURREAL-SCHEMA-HINTS.md §6.2.")]
    [SurrealNote("These two RELATION tables intentionally carry no extra fields (the scalars on quest_run / quest_node_execution carry the per-edge metadata).")]
    [Slice("quest")]
    [RelateEdge(typeof(QuestRun), typeof(QuestRun))]
    public partial class ForkedFrom : ISurrealRecord
    {
        public const string SchemaNameConst = "forked_from";
        public string SchemaName => SchemaNameConst;

        [Column(Order = 1, Name = "in", Type = "string")]
        [FieldGroup("RELATION quest_run -> quest_run (lineage). No extra fields.")]
        [JsonPropertyName("in")]
        public string In { get; set; } = string.Empty;

        [Column(Order = 2, Name = "out", Type = "string")]
        [JsonPropertyName("out")]
        public string Out { get; set; } = string.Empty;
    }
}
