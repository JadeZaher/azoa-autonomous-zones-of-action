// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the action_run table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("action_run",
        Aggregate = "ActionRun (Models/Actions/ActionRun.cs)",
        Guardrail = "G6 SCHEMAFULL; runtime canonical lineage carrier. One row per execution attempt of an action.")]
    [SurrealNote("Replaces the in-place mutation of ActionDefinition.Status that prevented re-runs from preserving prior attempt state -- see action-temporal-fork-model ADR §1. Re-running a Succeeded action creates a NEW root run (parent_run_id = null); forking a Running run creates a CHILD run with parent_run_id set.")]
    [SurrealNote("Lineage forms a TREE via parent_run_id, not a DAG. The forked_from RELATE edge (defined separately in surrealdb-migration §6.1) mirrors parent_run_id for native graph-traversal queries. Both must be kept in sync at write time.")]
    [SurrealNote("State machine invariants are enforced in ActionManager, not in the DB: Pending->Running (first claim), Running->{Succeeded|Failed|Forked|Cancelled} (terminal). fail_reason carries the supervisor-driven audit string; internal-error failures leave fail_reason null and write the error on the failed action_node_execution row instead.")]
    [Slice("action")]
    [Index("action_run_by_action", Fields = new[] { "action_id" })]
    [Index("action_run_by_user", Fields = new[] { "user_id" })]
    [Index("action_run_by_status", Fields = new[] { "status" })]
    [Index("action_run_by_parent", Fields = new[] { "parent_run_id" })]
    [Index("action_run_by_source_action", Fields = new[] { "source_action_id" })]
    [Index("action_run_by_origin_user", Fields = new[] { "origin_user_id" })]
    public partial class ActionRun : ISurrealRecord
    {
        public const string SchemaNameConst = "action_run";
        public string SchemaName => SchemaNameConst;

        public enum ActionRunStatusKind
        {
            Pending,
            Running,
            Succeeded,
            Failed,
            Forked,
            Cancelled,
            // Non-terminal park states (durable-workflow-engine): the run suspends
            // here awaiting an external advance/signal/timer/reconciliation and
            // resumes — they are NOT terminal. Mirror Models/Actions/ActionRunStatus.cs.
            // (These were previously absent from the POCO + the DB ASSERT, so a
            // durable park write would have been rejected by SCHEMAFULL.)
            Suspended,
            AwaitingSignal,
            AwaitingTimer,
            AwaitingReconciliation,
        }

        [Id]
        [FieldGroup("Run identity (record id is the Guid('N') of ActionRun.Id) -- unique across all runs of all actions")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Action definition this run executes")]
        [References(typeof(ActionDefinition))]
        public string ActionId { get; set; } = string.Empty;

        [FieldGroup("User that initiated this run (denormalized for query convenience)")]
        [References(typeof(User))]
        public string UserId { get; set; } = string.Empty;

        [FieldGroup("Tenant that drove this run via a tenant-driven child credential; null = user-driven. Carried on the durable row so the Tier-2 economic node handlers can stamp it on the produced BlockchainOperation and the custody signing seam's live consent check survives the async saga hop (tenant-consent-delegation AC4/AC4b). Mirrors OperationLog.acting_tenant_id.")]
        [References(typeof(User), Optional = true)]
        public string? ActingTenantId { get; set; }

        [FieldGroup("ActionRunStatus enum name")]
        [Inside("Pending", "Running", "Succeeded", "Failed", "Forked", "Cancelled",
                "Suspended", "AwaitingSignal", "AwaitingTimer", "AwaitingReconciliation")]
        [Default("\"Pending\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ActionRunStatusKind Status { get; set; }

        [FieldGroup("Wall-clock time at which the run row was created")]
        [ReadOnly]
        public DateTimeOffset StartedAt { get; set; }

        [FieldGroup("Wall-clock time at which the run reached a terminal state (null while non-terminal)")]
        public DateTimeOffset? EndedAt { get; set; }

        [FieldGroup("Parent run id if forked from another; null for root runs")]
        [References(typeof(ActionRun), Optional = true)]
        public string? ParentRunId { get; set; }

        [FieldGroup("Node at which the fork occurred (set iff this is a child fork run)")]
        [References(typeof(ActionNode), Optional = true)]
        public string? ForkedAtNodeId { get; set; }

        [FieldGroup("Free-form audit reason supplied when fork was triggered")]
        public string? ForkReason { get; set; }

        [FieldGroup("Free-form audit reason when a supervisor explicitly marked the run failed (distinct from internal-error path)")]
        public string? FailReason { get; set; }

        [FieldGroup("Marketplace provenance: the origin action definition this run was started from when the runner is not the action owner (null for owner runs). See Managers/AGENTS.md §publish-lifecycle.")]
        [References(typeof(ActionDefinition), Optional = true)]
        public string? SourceActionId { get; set; }

        [FieldGroup("Marketplace provenance: the origin action's owner/creator user when the runner is not the owner (null for owner runs).")]
        [References(typeof(User), Optional = true)]
        public string? OriginUserId { get; set; }

        [FieldGroup("The action's published_version_hash at the instant this run STARTED -- binds the run to the exact graph revision the runner saw/consented to. A run whose stamp no longer matches the live action is detectable evidence of an unpublish->edit->republish bait-and-switch. See Managers/AGENTS.md §published-version-hash.")]
        public string? PublishedVersionHash { get; set; }
    }
}
