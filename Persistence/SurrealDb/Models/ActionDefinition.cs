// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the action table.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("action",
        Aggregate = "ActionDefinition (Models/Actions/ActionDefinition.cs)",
        Guardrail = "G6 SCHEMAFULL; definition-side workflow shape. Runtime state lives on action_run + action_node_execution per action-temporal-fork-model ADR §1.")]
    [SurrealNote("Shape-only definition: name, owner, template ancestry, dapp-series binding, free-form metadata. completed_date was removed when action-temporal-fork-model split runtime from definition. status was also removed then but is reintroduced by action-dag-semantic-hardening FR-2 for the DEFINITION lifecycle (Draft/Active) -- distinct from the runtime ActionRun.Status. See Managers/AGENTS.md §publish-lifecycle.")]
    [SurrealNote("nodes/edges/dependencies live in their own tables (action_node, action_edge, action_dependency) keyed by action_id -- they are queried independently of the parent and mutated through Add/Remove endpoints. Embedding them would force the controller-side node/edge CRUD endpoints to read-modify-write the entire action row.")]
    [SurrealNote("metadata is a free-form string->string map for caller-supplied tags (e.g. UI color, sort hint). Persisted as a SurrealDB object to keep arbitrary keys queryable.")]
    [Slice("action")]
    [ExtraSurrealField("embedding", "option<array<float, 384>>", Order = 9, FieldGroup = "Embedding vector for HNSW semantic search (384-dimensional, MiniLM-style)")]
    [Index("action_by_user", Fields = new[] { "user_id" })]
    [Index("action_by_template", Fields = new[] { "template_id" })]
    [Index("action_by_dapp_series", Fields = new[] { "dapp_series_id" })]
    public partial class ActionDefinition : ISurrealRecord
    {
        public const string SchemaNameConst = "action";
        public string SchemaName => SchemaNameConst;

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of ActionDefinition.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Owner user (Guid('N') hex)")]
        [References(typeof(User))]
        public string UserId { get; set; } = string.Empty;

        [FieldGroup("Caller-supplied label")]
        [Required(NotEmpty = true)]
        public string Name { get; set; } = string.Empty;

        [FieldGroup("Optional description")]
        public string? Description { get; set; }

        [FieldGroup("Source template id when instantiated from an ActionTemplate (null for hand-authored actions)")]
        [References(typeof(ActionTemplate), Optional = true)]
        public string? TemplateId { get; set; }

        [FieldGroup("Owning DappSeries when this action is part of a composed dApp (null for standalone actions)")]
        [References(typeof(DappSeries), Optional = true)]
        public string? DappSeriesId { get; set; }

        [Column(Flexible = true)]
        [FieldGroup("Free-form caller-supplied metadata (string->string map)")]
        public JsonElement Metadata { get; set; }

        [FieldGroup("Definition lifecycle: Draft (editable) or Active (locked for execution). See Managers/AGENTS.md §publish-lifecycle.")]
        [Inside("Draft", "Active", "Completed", "Failed", "Archived")]
        [Default("\"Draft\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ActionStatusKind Status { get; set; }

        [FieldGroup("Optimistic-concurrency token for the definition lifecycle (F6 TOCTOU guard). Bumped on every CAS status transition. See Managers/AGENTS.md §publish-lifecycle.")]
        [JsonPropertyName("version")]
        [Default("0")]
        public long Version { get; set; }

        [FieldGroup("Marketplace visibility: false = private (owner-only), true = published so a non-owner can start it under their own context. See Managers/AGENTS.md §publish-lifecycle.")]
        [Default("false")]
        public bool IsPublic { get; set; }

        [FieldGroup("Denormalised origin/creator user when this action is a marketplace copy (null for first-party actions). Provenance back to the action this was cloned/instantiated from.")]
        [References(typeof(User), Optional = true)]
        public string? OriginUserId { get; set; }

        [FieldGroup("Content hash of the node/edge graph as of the last publish (bait-and-switch guard). Null while never published; recomputed on every PublishAsync. Stamped onto each action_run at start so a run is bound to the exact graph revision the runner saw. See Managers/AGENTS.md §published-version-hash.")]
        public string? PublishedVersionHash { get; set; }

        [FieldGroup("Run-authorization dimension, orthogonal to is_public (discoverability). Open = anyone who can view may run; InviteOnly = owner + invited_user_ids only. See Persistence/SurrealDb/Models/AGENTS.md §action-access-request.")]
        [Inside("Open", "InviteOnly")]
        [Default("\"Open\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ActionRunAccessKind RunAccess { get; set; }

        [FieldGroup("Approved invite set for InviteOnly actions (user-id hex); owner is always implicitly invited.")]
        public IReadOnlyList<string>? InvitedUserIds { get; set; }

        [FieldGroup("Definition birthdate -- STAYS on the definition, not a runtime artifact")]
        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        /// <summary>Mirrors the full domain ActionStatus enum (action-dag-semantic-hardening FR-2).
        /// [Inside] values must stay in sync with ActionStatus in Models/Actions/ActionEnums.cs.</summary>
        public enum ActionStatusKind
        {
            Draft,
            Active,
            Completed,
            Failed,
            Archived
        }

        /// <summary>Mirrors the domain ActionRunAccess enum. [Inside] values must stay in sync with ActionRunAccess in Models/Actions/ActionEnums.cs.</summary>
        public enum ActionRunAccessKind
        {
            Open,
            InviteOnly
        }
    }
}
