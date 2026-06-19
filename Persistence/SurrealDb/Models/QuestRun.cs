// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest_run table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("quest_run",
        Aggregate = "QuestRun (Models/Quest/QuestRun.cs)",
        Guardrail = "G6 SCHEMAFULL; runtime canonical lineage carrier. One row per execution attempt of a quest.")]
    [SurrealNote("Replaces the in-place mutation of Quest.Status that prevented re-runs from preserving prior attempt state -- see quest-temporal-fork-model ADR §1. Re-running a Succeeded quest creates a NEW root run (parent_run_id = null); forking a Running run creates a CHILD run with parent_run_id set.")]
    [SurrealNote("Lineage forms a TREE via parent_run_id, not a DAG. The forked_from RELATE edge (defined separately in surrealdb-migration §6.1) mirrors parent_run_id for native graph-traversal queries. Both must be kept in sync at write time.")]
    [SurrealNote("State machine invariants are enforced in QuestManager, not in the DB: Pending->Running (first claim), Running->{Succeeded|Failed|Forked|Cancelled} (terminal). fail_reason carries the supervisor-driven audit string; internal-error failures leave fail_reason null and write the error on the failed quest_node_execution row instead.")]
    [Slice("quest")]
    [Index("quest_run_by_quest", Fields = new[] { "quest_id" })]
    [Index("quest_run_by_avatar", Fields = new[] { "avatar_id" })]
    [Index("quest_run_by_status", Fields = new[] { "status" })]
    [Index("quest_run_by_parent", Fields = new[] { "parent_run_id" })]
    public partial class QuestRun : ISurrealRecord
    {
        public const string SchemaNameConst = "quest_run";
        public string SchemaName => SchemaNameConst;

        public enum QuestRunStatusKind
        {
            Pending,
            Running,
            Succeeded,
            Failed,
            Forked,
            Cancelled,
        }

        [Id, Column(Order = 1, Type = "string")]
        [FieldGroup("Run identity (record id is the Guid('N') of QuestRun.Id) -- unique across all runs of all quests")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [FieldGroup("Quest definition this run executes")]
        [References(typeof(Quest))]
        [JsonPropertyName("quest_id")]
        public string QuestId { get; set; } = string.Empty;

        [Column(Order = 3)]
        [FieldGroup("Avatar that initiated this run (denormalized for query convenience)")]
        [References(typeof(Avatar))]
        [JsonPropertyName("avatar_id")]
        public string AvatarId { get; set; } = string.Empty;

        [Column(Order = 4, Type = "string")]
        [FieldGroup("QuestRunStatus enum name")]
        [Inside("Pending", "Running", "Succeeded", "Failed", "Forked", "Cancelled")]
        [Default("\"Pending\"")]
        [JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter))]
        public QuestRunStatusKind Status { get; set; }

        [Column(Order = 5, Type = "datetime")]
        [FieldGroup("Wall-clock time at which the run row was created")]
        [ReadOnly]
        [JsonPropertyName("started_at")]
        public DateTimeOffset StartedAt { get; set; }

        [Column(Order = 6, Type = "option<datetime>")]
        [FieldGroup("Wall-clock time at which the run reached a terminal state (null while non-terminal)")]
        [JsonPropertyName("ended_at")]
        public DateTimeOffset? EndedAt { get; set; }

        [Column(Order = 7)]
        [FieldGroup("Parent run id if forked from another; null for root runs")]
        [References(typeof(QuestRun), Optional = true)]
        [JsonPropertyName("parent_run_id")]
        public string? ParentRunId { get; set; }

        [Column(Order = 8)]
        [FieldGroup("Node at which the fork occurred (set iff this is a child fork run)")]
        [References(typeof(QuestNode), Optional = true)]
        [JsonPropertyName("forked_at_node_id")]
        public string? ForkedAtNodeId { get; set; }

        [Column(Order = 9, Type = "option<string>")]
        [FieldGroup("Free-form audit reason supplied when fork was triggered")]
        [JsonPropertyName("fork_reason")]
        public string? ForkReason { get; set; }

        [Column(Order = 10, Type = "option<string>")]
        [FieldGroup("Free-form audit reason when a supervisor explicitly marked the run failed (distinct from internal-error path)")]
        [JsonPropertyName("fail_reason")]
        public string? FailReason { get; set; }
    }
}
