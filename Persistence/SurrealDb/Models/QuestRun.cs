// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest_run table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
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
            // Non-terminal park states (durable-workflow-engine): the run suspends
            // here awaiting an external advance/signal/timer/reconciliation and
            // resumes — they are NOT terminal. Mirror Models/Quest/QuestRunStatus.cs.
            // (These were previously absent from the POCO + the DB ASSERT, so a
            // durable park write would have been rejected by SCHEMAFULL.)
            Suspended,
            AwaitingSignal,
            AwaitingTimer,
            AwaitingReconciliation,
        }

        [Id]
        [FieldGroup("Run identity (record id is the Guid('N') of QuestRun.Id) -- unique across all runs of all quests")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Quest definition this run executes")]
        [References(typeof(Quest))]
        public string QuestId { get; set; } = string.Empty;

        [FieldGroup("Avatar that initiated this run (denormalized for query convenience)")]
        [References(typeof(Avatar))]
        public string AvatarId { get; set; } = string.Empty;

        [FieldGroup("Tenant that drove this run via a tenant-driven child credential; null = user-driven. Carried on the durable row so the Tier-2 economic node handlers can stamp it on the produced BlockchainOperation and the custody signing seam's live consent check survives the async saga hop (tenant-consent-delegation AC4/AC4b). Mirrors OperationLog.acting_tenant_id.")]
        [References(typeof(Avatar), Optional = true)]
        public string? ActingTenantId { get; set; }

        [FieldGroup("QuestRunStatus enum name")]
        [Inside("Pending", "Running", "Succeeded", "Failed", "Forked", "Cancelled",
                "Suspended", "AwaitingSignal", "AwaitingTimer", "AwaitingReconciliation")]
        [Default("\"Pending\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public QuestRunStatusKind Status { get; set; }

        [FieldGroup("Wall-clock time at which the run row was created")]
        [ReadOnly]
        public DateTimeOffset StartedAt { get; set; }

        [FieldGroup("Wall-clock time at which the run reached a terminal state (null while non-terminal)")]
        public DateTimeOffset? EndedAt { get; set; }

        [FieldGroup("Parent run id if forked from another; null for root runs")]
        [References(typeof(QuestRun), Optional = true)]
        public string? ParentRunId { get; set; }

        [FieldGroup("Node at which the fork occurred (set iff this is a child fork run)")]
        [References(typeof(QuestNode), Optional = true)]
        public string? ForkedAtNodeId { get; set; }

        [FieldGroup("Free-form audit reason supplied when fork was triggered")]
        public string? ForkReason { get; set; }

        [FieldGroup("Free-form audit reason when a supervisor explicitly marked the run failed (distinct from internal-error path)")]
        public string? FailReason { get; set; }
    }
}
