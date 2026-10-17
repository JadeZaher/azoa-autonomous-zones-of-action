// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the action_access_request table.

#nullable enable

using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("action_access_request",
        Aggregate = "ActionAccessRequest (Models/Actions/ActionAccessRequest.cs)",
        Guardrail = "G6 SCHEMAFULL; one row per (action, requester) access request. Owner approval mints an invite (appends to action.invited_user_ids).")]
    [SurrealNote("Request/approve flow for InviteOnly actions (action-invitations-approval track). State machine + idempotency invariant live in Persistence/SurrealDb/Models/AGENTS.md §action-access-request: at most ONE non-terminal (Pending) request per (action_id, requester_user_id); Pending->{Approved|Rejected|Withdrawn} are terminal + immutable; a re-request after a terminal state opens a fresh Pending. Transitions are enforced in the manager, not the DB.")]
    [Slice("action")]
    [Index("action_access_request_by_action", Fields = new[] { "action_id" })]
    [Index("action_access_request_by_requester", Fields = new[] { "requester_user_id" })]
    public partial class ActionAccessRequest : ISurrealRecord
    {
        public const string SchemaNameConst = "action_access_request";
        public string SchemaName => SchemaNameConst;

        /// <summary>Mirrors the domain ActionAccessRequestStatus enum. [Inside] values must stay in sync with ActionAccessRequestStatus in Models/Actions/ActionEnums.cs.</summary>
        public enum ActionAccessRequestStatusKind
        {
            Pending,
            Approved,
            Rejected,
            Withdrawn
        }

        [Id]
        [FieldGroup("Request identity (record id is the Guid('N') of ActionAccessRequest.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Action this request targets")]
        [References(typeof(ActionDefinition))]
        public string ActionId { get; set; } = string.Empty;

        [FieldGroup("User requesting access")]
        [References(typeof(User))]
        public string RequesterUserId { get; set; } = string.Empty;

        [FieldGroup("ActionAccessRequestStatus enum name")]
        [Inside("Pending", "Approved", "Rejected", "Withdrawn")]
        [Default("\"Pending\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ActionAccessRequestStatusKind Status { get; set; }

        [FieldGroup("Optional requester note supplied at request time")]
        public string? Message { get; set; }

        [FieldGroup("Optional owner-supplied reason recorded on approve/reject")]
        public string? DecisionReason { get; set; }

        [FieldGroup("Wall-clock time at which the request row was created")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        [FieldGroup("Wall-clock time at which the request reached a terminal state (null while Pending)")]
        public DateTimeOffset? DecidedAt { get; set; }

        [FieldGroup("User that decided the request (owner for approve/reject; requester for withdraw); null while Pending")]
        [References(typeof(User), Optional = true)]
        public string? DecidedByUserId { get; set; }
    }
}
