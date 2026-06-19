// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest_node_execution table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("quest_node_execution",
        Aggregate = "QuestNodeExecution (Models/Quest/QuestNodeExecution.cs)",
        Guardrail = "G6 SCHEMAFULL; G2 exactly-once claim primitive (api-safety-hardening). One row per (run, node).")]
    [SurrealNote("Per-(run, node) execution row. Replaces the in-place mutation of QuestNode.State/Output/Error which prevented re-runs from preserving prior attempt's outputs.")]
    [SurrealNote("On a fork, the same execution row can be referenced by both parent and child run via the `executes` RELATE edge for nodes whose execution_order < forkPoint (copy-by-reference, no duplication). Scalar run_id is the originating run; cross-run reference happens through the RELATE edge defined separately in surrealdb-migration §6.2.")]
    [SurrealNote("The G2 claim primitive (TryClaimPendingAsync) is a conditional UPDATE that only succeeds when current state == 'Pending'. Empty-result case = lost race (already claimed by another worker). The (run_id, node_id) UNIQUE index is what makes the claim atomically safe.")]
    [SurrealNote("output stores the JSON-serialized OASISResult<T> from the handler call. error carries the failure message when state = Failed. Both are null in non-terminal states.")]
    [Slice("quest")]
    [Index("quest_node_execution_run_node", Fields = new[] { "run_id", "node_id" }, Unique = true)]
    [Index("quest_node_execution_by_run", Fields = new[] { "run_id" })]
    [Index("quest_node_execution_by_node", Fields = new[] { "node_id" })]
    [Index("quest_node_execution_by_state", Fields = new[] { "state" })]
    public partial class QuestNodeExecution : ISurrealRecord
    {
        public const string SchemaNameConst = "quest_node_execution";
        public string SchemaName => SchemaNameConst;

        public enum QuestNodeState
        {
            Pending,
            Running,
            Succeeded,
            Failed,
            Skipped,
            Cancelled,
        }

        [Id, Column(Order = 1, Type = "string")]
        [FieldGroup("Execution row identity (record id is the Guid('N') of QuestNodeExecution.Id)")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [FieldGroup("Owning run (originating run; cross-run references via `executes` RELATE edge)")]
        [References(typeof(QuestRun))]
        [JsonPropertyName("run_id")]
        public string RunId { get; set; } = string.Empty;

        [Column(Order = 3)]
        [FieldGroup("Quest definition node this execution corresponds to")]
        [References(typeof(QuestNode))]
        [JsonPropertyName("node_id")]
        public string NodeId { get; set; } = string.Empty;

        [Column(Order = 4, Type = "string")]
        [FieldGroup("QuestNodeState enum name")]
        [Inside("Pending", "Running", "Succeeded", "Failed", "Skipped", "Cancelled")]
        [Default("\"Pending\"")]
        [JsonPropertyName("state"), JsonConverter(typeof(JsonStringEnumConverter))]
        public QuestNodeState State { get; set; }

        [Column(Order = 5, Type = "option<string>")]
        [FieldGroup("Serialized OASISResult<T> from the handler call (null until Succeeded)")]
        [JsonPropertyName("output")]
        public string? Output { get; set; }

        [Column(Order = 6, Type = "option<string>")]
        [FieldGroup("Failure message when state is Failed")]
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [Column(Order = 7, Type = "datetime")]
        [FieldGroup("Wall-clock time at which the row entered Running")]
        // NOT [ReadOnly]: started_at is set at the Pending->Running CLAIM
        // (TryClaimPendingAsync UPDATE ... SET started_at = $_now), not at row
        // creation — so it is legitimately written once AFTER insert. READONLY
        // would reject the claim UPDATE (verified via integration test). Unlike
        // the other 17 models whose creation timestamp is set at CREATE time.
        [JsonPropertyName("started_at")]
        public DateTimeOffset StartedAt { get; set; }

        [Column(Order = 8, Type = "option<datetime>")]
        [FieldGroup("Wall-clock time at which the row reached a terminal state (null while non-terminal)")]
        [JsonPropertyName("ended_at")]
        public DateTimeOffset? EndedAt { get; set; }
    }
}
