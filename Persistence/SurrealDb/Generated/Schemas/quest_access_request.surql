-- ============================================================
-- Table: quest_access_request
-- Aggregate: QuestAccessRequest (Models/Quest/QuestAccessRequest.cs)
-- Guardrail: G6 SCHEMAFULL; one row per (quest, requester) access request. Owner approval mints an invite (appends to quest.invited_avatar_ids).
-- Note: Request/approve flow for InviteOnly quests (quest-invitations-approval track). State machine + idempotency invariant live in Persistence/SurrealDb/Models/AGENTS.md §quest-access-request: at most ONE non-terminal (Pending) request per (quest_id, requester_avatar_id); Pending->{Approved|Rejected|Withdrawn} are terminal + immutable; a re-request after a terminal state opens a fresh Pending. Transitions are enforced in the manager, not the DB.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- QuestAccessRequest.QuestAccessRequestStatusKind
DEFINE PARAM IF NOT EXISTS $quest_access_request_status VALUE ["Pending", "Approved", "Rejected", "Withdrawn"];

DEFINE TABLE IF NOT EXISTS quest_access_request SCHEMAFULL;

-- Request identity (record id is the Guid('N') of QuestAccessRequest.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE quest_access_request TYPE string
    ASSERT $value != NONE AND $value != "";

-- Quest this request targets
DEFINE FIELD IF NOT EXISTS quest_id ON TABLE quest_access_request TYPE record<quest>;

-- Avatar requesting access
DEFINE FIELD IF NOT EXISTS requester_avatar_id ON TABLE quest_access_request TYPE record<avatar>;

-- QuestAccessRequestStatus enum name
DEFINE FIELD IF NOT EXISTS status ON TABLE quest_access_request TYPE string
    ASSERT $value INSIDE $quest_access_request_status
    DEFAULT "Pending";

-- Optional requester note supplied at request time
DEFINE FIELD IF NOT EXISTS message ON TABLE quest_access_request TYPE option<string>;

-- Optional owner-supplied reason recorded on approve/reject
DEFINE FIELD IF NOT EXISTS decision_reason ON TABLE quest_access_request TYPE option<string>;

-- Wall-clock time at which the request row was created
DEFINE FIELD IF NOT EXISTS created_at ON TABLE quest_access_request TYPE datetime
    READONLY;

-- Wall-clock time at which the request reached a terminal state (null while Pending)
DEFINE FIELD IF NOT EXISTS decided_at ON TABLE quest_access_request TYPE option<datetime>;

-- Avatar that decided the request (owner for approve/reject; requester for withdraw); null while Pending
DEFINE FIELD IF NOT EXISTS decided_by_avatar_id ON TABLE quest_access_request TYPE option<record<avatar>>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS quest_access_request_by_quest
    ON TABLE quest_access_request
    FIELDS quest_id;

DEFINE INDEX IF NOT EXISTS quest_access_request_by_requester
    ON TABLE quest_access_request
    FIELDS requester_avatar_id;
