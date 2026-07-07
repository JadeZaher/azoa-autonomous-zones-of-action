---
type: spec
track: quest-invitations-approval
created: 2026-07-07
status: pending
horizon: post-launch
depends_on: [marketplace-guard-hardening]
---

# quest-invitations-approval — invite-gated quests + request/approve flow

## Why

Today a quest is a binary: private (owner-only) or `IsPublic` (anyone published +
Active may run/fork). The ArdaNova many-devs-one-node model needs a middle tier: a
quest that is **publicly discoverable but runnable only by invited avatars**, plus a
self-service path for an interested avatar to **request access** and for the owner to
**approve/reject** — an approval flow that mints an invitation.

Critically this is NOT "make everything invite-only": some quests stay **Open**
(anyone can take, no request needed — today's behavior). The new dimension is
*run-authorization*, orthogonal to *discoverability* (`IsPublic`).

## Model (decided)

Keep `Quest.IsPublic` as the **discoverability** flag (does it show in the
marketplace / is its definition viewable by non-owners). Add a **run-access**
dimension:

- `Quest.RunAccess : QuestRunAccess` enum — `Open` (default; anyone who can view may
  run — today's semantics) | `InviteOnly` (only owner + invited avatars may
  run/fork).
- `Quest.InvitedAvatarIds : List<Guid>` — the approved invite set (owner is always
  implicitly invited).

A **public + InviteOnly** quest is viewable by all (marketplace + full node graph +
economic preview) but startable/forkable only by owner + invited. A **public + Open**
quest is exactly today's behavior. A **private** quest stays owner-only regardless of
`RunAccess`.

### Request / approval state machine

New entity `QuestAccessRequest`:
- Fields: `Id`, `QuestId`, `RequesterAvatarId`, `Status : QuestAccessRequestStatus`
  (`Pending | Approved | Rejected | Withdrawn`), `Message?` (requester note),
  `DecisionReason?`, `CreatedAt`, `DecidedAt?`, `DecidedByAvatarId?`.
- Transitions: `Pending → Approved` (owner approves → append RequesterAvatarId to
  `Quest.InvitedAvatarIds`), `Pending → Rejected` (owner rejects), `Pending →
  Withdrawn` (requester cancels). Terminal states are immutable.
- Idempotency: at most ONE non-terminal (`Pending`) request per
  (QuestId, RequesterAvatarId). A re-request while Pending returns the existing one;
  a re-request after Rejected/Withdrawn opens a fresh Pending.
- Already-invited or owner requesting → rejected at the API with a clear message (no
  request needed).

## Backend

### Data model
- `Models/Quest/QuestEnums.cs` — add `QuestRunAccess { Open, InviteOnly }` and
  `QuestAccessRequestStatus { Pending, Approved, Rejected, Withdrawn }`.
- `Models/Quest/Quest.cs` — add `RunAccess` (default `Open`) + `InvitedAvatarIds`.
- `Models/Quest/QuestAccessRequest.cs` — new entity (above).
- SurrealDB: `Persistence/SurrealDb/Models/Quest.cs` — add `run_access` +
  `invited_avatar_ids` decorated fields; NEW `QuestAccessRequest` POCO +
  `quest_access_request` table. Regenerate goldens
  (`Persistence/SurrealDb/Generated/Schemas/*.surql`) via `AZOA_REGENERATE_GOLDENS=1`
  — never hand-edit. New `SurrealQuestAccessRequestStore` + `IQuestAccessRequestStore`
  mirroring existing store shape; both mapper directions.

### Authorization chokepoint (the load-bearing change)
- `QuestManager.LoadStartableQuestAsync` (QuestManager.cs:106-123) is the single
  run-start gate for BOTH `ExecuteAsync` and `StartWorkflowRunAsync`. After the
  existing `IsPublic` + `Active` checks (line 122), add: **if `RunAccess ==
  InviteOnly` and caller is not owner and `!InvitedAvatarIds.Contains(avatarId)` →
  reject** with an actionable message ("This quest requires an invitation — request
  access"). Owner path (line 113-114) is unaffected.
- `ForkAsync` already routes ownership through `LoadOwnedRunAsync` (forker owns their
  run) — but a fork re-runs the quest's nodes, so add the SAME invite check there for
  the ORIGINATING quest when the run's avatar is a non-owner on an InviteOnly quest.
  (Confirm during impl: a runner who was invited, ran, then lost invitation — decision:
  an in-flight run may finish + fork; new starts are gated. Document in AGENTS.md.)
- View gate is unchanged: `GetAsync` stays `owner || IsPublic` — InviteOnly does NOT
  restrict viewing, only running.

### Manager + endpoints (QuestController)
- `SetRunAccessAsync(questId, ownerAvatarId, RunAccess, invitedAvatarIds?)` — owner
  sets mode + optionally seeds the invite list. Owner-only (LoadOwnedQuest).
- `InviteAvatarAsync` / `RevokeInviteAsync(questId, ownerAvatarId, targetAvatarId)` —
  owner directly adds/removes an invite (no request needed).
- `RequestAccessAsync(questId, requesterAvatarId, message?)` — any avatar who can VIEW
  the quest opens a Pending request (idempotent per above). Rejects owner/already-
  invited.
- `ListAccessRequestsAsync(questId, ownerAvatarId, status?)` — owner sees the approval
  queue (owner-only).
- `DecideAccessRequestAsync(requestId, ownerAvatarId, approve, reason?)` — owner
  approve→invite / reject. Owner-only, scoped by the request's quest owner.
- `WithdrawAccessRequestAsync(requestId, requesterAvatarId)` — requester cancels their
  own Pending request.
- `ListMyAccessRequestsAsync(requesterAvatarId, status?)` — requester's own outbound
  requests.
- New routes on `QuestController`: `PUT /{id}/run-access`, `POST /{id}/invite`,
  `DELETE /{id}/invite/{avatarId}`, `POST /{id}/access-requests`,
  `GET /{id}/access-requests`, `POST /access-requests/{requestId}/decision`,
  `POST /access-requests/{requestId}/withdraw`, `GET /access-requests/mine`. IDOR-
  scoped (owner routes by quest-owner; requester routes by requester identity; body
  avatar ignored).

## SDK (sdk/azoa-wallet)
- `api/api-version.ts` — add the paths above (QUEST_RUN_ACCESS, QUEST_INVITE,
  QUEST_ACCESS_REQUESTS, ACCESS_REQUEST_DECISION, etc.).
- Typed client methods mirroring each endpoint (setRunAccess, inviteAvatar,
  revokeInvite, requestQuestAccess, listAccessRequests, decideAccessRequest,
  withdrawAccessRequest, listMyAccessRequests). Types for `QuestRunAccess`,
  `QuestAccessRequest`, request/response DTOs. `assertUuid` on all URL ids. `npm run
  build` (tsup) after — the frontend consumes the BUILT package.

## Frontend (frontend/src)
- `app/(dashboard)/quests/page.tsx` (marketplace/quest list) — badge each quest as
  Open / Invite-only; for InviteOnly non-invited quests show a **"Request to take"**
  button (or "Requested — pending" / "Approved — run" per current state); Open quests
  keep the direct Run/Fork action.
- Quest detail / builder owner surface — a **run-access toggle** (Open ↔ InviteOnly),
  an **invite manager** (add/remove avatars directly), and an **approval queue** panel
  listing Pending requests with Approve/Reject (+ optional reason).
- A **"My requests"** view for the requester to see pending/approved/rejected outbound
  requests and withdraw a pending one.
- Wire through the SDK singleton (`frontend/src/lib/oasis.ts`) + hooks
  (`oasis-hooks.ts`). NOTE: per project rule, do NOT run frontend typecheck — build
  SDK tsc + dotnet only.

## Acceptance criteria
- AC1: A public+Open quest runs/forks for any avatar exactly as today (no regression).
- AC2: A public+InviteOnly quest is viewable by a non-invited avatar but
  run/fork is rejected with an actionable message.
- AC3: A non-invited avatar can open exactly one Pending request; owner approve appends
  them to InvitedAvatarIds and they can then run; reject/withdraw closes it; re-request
  after a terminal state opens a fresh Pending.
- AC4: Owner can directly invite/revoke without a request; revoke blocks future starts
  (in-flight runs unaffected — documented).
- AC5: All new routes are IDOR-scoped (owner routes reject non-owners; requester routes
  reject other requesters; body-supplied avatar ignored). A non-owner cannot list/decide
  another quest's requests.
- AC6: Backend build clean, unit tests green including new tests for LoadStartableQuest
  invite-gate, the request state machine (idempotency + terminal immutability), and
  IDOR scoping. Goldens regenerated, not hand-edited.

## Out of scope (follow-ups)
- Notifications/email on request/approval (event only; delivery later).
- Bulk invite / invite-by-link tokens.
- Time-boxed or single-use invitations.
- The `CanRead` predicate dedup (tracked separately; touch it here only if an invite
  check naturally lands beside it).
