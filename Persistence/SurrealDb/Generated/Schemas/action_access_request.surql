-- ============================================================
-- Table: action_access_request
-- Aggregate: ActionAccessRequest (Models/Actions/ActionAccessRequest.cs)
-- Guardrail: G6 SCHEMAFULL; one row per (action, requester) access request. Owner approval mints an invite (appends to action.invited_user_ids).
-- Note: Request/approve flow for InviteOnly actions (action-invitations-approval track). State machine + idempotency invariant live in Persistence/SurrealDb/Models/AGENTS.md §action-access-request: at most ONE non-terminal (Pending) request per (action_id, requester_user_id); Pending->{Approved|Rejected|Withdrawn} are terminal + immutable; a re-request after a terminal state opens a fresh Pending. Transitions are enforced in the manager, not the DB.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- ActionAccessRequest.ActionAccessRequestStatusKind
DEFINE PARAM IF NOT EXISTS $action_access_request_status VALUE ["Pending", "Approved", "Rejected", "Withdrawn"];

DEFINE TABLE IF NOT EXISTS action_access_request SCHEMAFULL;

-- Request identity (record id is the Guid('N') of ActionAccessRequest.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE action_access_request TYPE string
    ASSERT $value != NONE AND $value != "";

-- Action this request targets
DEFINE FIELD IF NOT EXISTS action_id ON TABLE action_access_request TYPE record<action>;

-- User requesting access
DEFINE FIELD IF NOT EXISTS requester_user_id ON TABLE action_access_request TYPE record<user>;

-- ActionAccessRequestStatus enum name
DEFINE FIELD IF NOT EXISTS status ON TABLE action_access_request TYPE string
    ASSERT $value INSIDE $action_access_request_status
    DEFAULT "Pending";

-- Optional requester note supplied at request time
DEFINE FIELD IF NOT EXISTS message ON TABLE action_access_request TYPE option<string>;

-- Optional owner-supplied reason recorded on approve/reject
DEFINE FIELD IF NOT EXISTS decision_reason ON TABLE action_access_request TYPE option<string>;

-- Wall-clock time at which the request row was created
DEFINE FIELD IF NOT EXISTS created_at ON TABLE action_access_request TYPE datetime
    READONLY;

-- Wall-clock time at which the request reached a terminal state (null while Pending)
DEFINE FIELD IF NOT EXISTS decided_at ON TABLE action_access_request TYPE option<datetime>;

-- User that decided the request (owner for approve/reject; requester for withdraw); null while Pending
DEFINE FIELD IF NOT EXISTS decided_by_user_id ON TABLE action_access_request TYPE option<record<user>>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS action_access_request_by_action
    ON TABLE action_access_request
    FIELDS action_id;

DEFINE INDEX IF NOT EXISTS action_access_request_by_requester
    ON TABLE action_access_request
    FIELDS requester_user_id;
