// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest_node_execution table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("quest_node_execution",
        Aggregate = "QuestNodeExecution (Models/Quest/QuestNodeExecution.cs)",
        Guardrail = "G6 SCHEMAFULL; G2 exactly-once claim primitive (api-safety-hardening). One row per (run, node).")]
    [SurrealNote("Per-(run, node) execution row. Replaces the in-place mutation of QuestNode.State/Output/Error which prevented re-runs from preserving prior attempt's outputs.")]
    [SurrealNote("On a fork, the same execution row can be referenced by both parent and child run via the `executes` RELATE edge for nodes whose execution_order < forkPoint (copy-by-reference, no duplication). Scalar run_id is the originating run; cross-run reference happens through the RELATE edge defined separately in surrealdb-migration §6.2.")]
    [SurrealNote("The G2 claim primitive (TryClaimPendingAsync) is a conditional UPDATE that only succeeds when current state == 'Pending'. Empty-result case = lost race (already claimed by another worker). The (run_id, node_id) UNIQUE index is what makes the claim atomically safe.")]
    [SurrealNote("output stores the JSON-serialized AZOAResult<T> from the handler call. error carries the failure message when state = Failed. Both are null in non-terminal states.")]
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

        [Id]
        [FieldGroup("Execution row identity (record id is the Guid('N') of QuestNodeExecution.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Owning run (originating run; cross-run references via `executes` RELATE edge)")]
        [References(typeof(QuestRun))]
        public string RunId { get; set; } = string.Empty;

        [FieldGroup("Quest definition node this execution corresponds to")]
        [References(typeof(QuestNode))]
        public string NodeId { get; set; } = string.Empty;

        [FieldGroup("QuestNodeState enum name")]
        [Inside("Pending", "Running", "Succeeded", "Failed", "Skipped", "Cancelled")]
        [Default("\"Pending\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public QuestNodeState State { get; set; }

        [FieldGroup("Serialized AZOAResult<T> from the handler call (null until Succeeded)")]
        public string? Output { get; set; }

        [FieldGroup("Failure message when state is Failed")]
        public string? Error { get; set; }

        [FieldGroup("Wall-clock time at which the row entered Running")]
        // NOT [ReadOnly]: started_at is set at the Pending->Running CLAIM
        // (TryClaimPendingAsync UPDATE ... SET started_at = $_now), not at row
        // creation — so it is legitimately written once AFTER insert. READONLY
        // would reject the claim UPDATE (verified via integration test). Unlike
        // the other 17 models whose creation timestamp is set at CREATE time.
        public DateTimeOffset StartedAt { get; set; }

        [FieldGroup("Wall-clock time at which the row reached a terminal state (null while non-terminal)")]
        public DateTimeOffset? EndedAt { get; set; }
    }
}
