// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest_access_request table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("quest_access_request",
        Aggregate = "QuestAccessRequest (Models/Quest/QuestAccessRequest.cs)",
        Guardrail = "G6 SCHEMAFULL; one row per (quest, requester) access request. Owner approval mints an invite (appends to quest.invited_avatar_ids).")]
    [SurrealNote("Request/approve flow for InviteOnly quests (quest-invitations-approval track). State machine + idempotency invariant live in Persistence/SurrealDb/Models/AGENTS.md §quest-access-request: at most ONE non-terminal (Pending) request per (quest_id, requester_avatar_id); Pending->{Approved|Rejected|Withdrawn} are terminal + immutable; a re-request after a terminal state opens a fresh Pending. Transitions are enforced in the manager, not the DB.")]
    [Slice("quest")]
    [Index("quest_access_request_by_quest", Fields = new[] { "quest_id" })]
    [Index("quest_access_request_by_requester", Fields = new[] { "requester_avatar_id" })]
    public partial class QuestAccessRequest : ISurrealRecord
    {
        public const string SchemaNameConst = "quest_access_request";
        public string SchemaName => SchemaNameConst;

        /// <summary>Mirrors the domain QuestAccessRequestStatus enum. [Inside] values must stay in sync with QuestAccessRequestStatus in Models/Quest/QuestEnums.cs.</summary>
        public enum QuestAccessRequestStatusKind
        {
            Pending,
            Approved,
            Rejected,
            Withdrawn
        }

        [Id]
        [FieldGroup("Request identity (record id is the Guid('N') of QuestAccessRequest.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Quest this request targets")]
        [References(typeof(Quest))]
        public string QuestId { get; set; } = string.Empty;

        [FieldGroup("Avatar requesting access")]
        [References(typeof(Avatar))]
        public string RequesterAvatarId { get; set; } = string.Empty;

        [FieldGroup("QuestAccessRequestStatus enum name")]
        [Inside("Pending", "Approved", "Rejected", "Withdrawn")]
        [Default("\"Pending\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public QuestAccessRequestStatusKind Status { get; set; }

        [FieldGroup("Optional requester note supplied at request time")]
        public string? Message { get; set; }

        [FieldGroup("Optional owner-supplied reason recorded on approve/reject")]
        public string? DecisionReason { get; set; }

        [FieldGroup("Wall-clock time at which the request row was created")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        [FieldGroup("Wall-clock time at which the request reached a terminal state (null while Pending)")]
        public DateTimeOffset? DecidedAt { get; set; }

        [FieldGroup("Avatar that decided the request (owner for approve/reject; requester for withdraw); null while Pending")]
        [References(typeof(Avatar), Optional = true)]
        public string? DecidedByAvatarId { get; set; }
    }
}
