# Persistence/SurrealDb/Models — decorated POCO notes

Design rationale / cross-cutting invariants for the hand-authored SurrealDB
POCOs in this directory. Code carries terse one-line doc-comments; the WHY
lives here. Goldens under `Generated/Schemas/*.surql` are GENERATED — never
hand-edit; regenerate via the byte-equivalence test with
`AZOA_REGENERATE_GOLDENS=1` (see `AttributePocoByteEquivalenceTests`).

Each `*Kind` enum inside a POCO MIRRORS its domain enum
(`Models/Quest/QuestEnums.cs`); the `[Inside(...)]` allow-list on the
string-enum field MUST enumerate exactly those names, or a SCHEMAFULL ASSERT
rejects a legitimate write.

## §quest-access-request

quest-invitations-approval track. Adds a **run-authorization** dimension to
`Quest`, orthogonal to `is_public` (discoverability):

- `quest.run_access` (`Open` | `InviteOnly`, default `Open`). `Open` is
  today's behavior — anyone who can view may run/fork. `InviteOnly` gates
  run/fork to the owner + `quest.invited_avatar_ids`. Viewing is unaffected:
  a public + InviteOnly quest is fully viewable by non-invited avatars; only
  starting/forking is gated.
- `quest.invited_avatar_ids` (`array<string>` of `avatar:hex` links) — the
  approved invite set. The owner is ALWAYS implicitly invited and is never
  written into this list.

`quest_access_request` is the self-service request/approve flow for an
InviteOnly quest.

### State machine (enforced in the manager, not the DB)

```
Pending ──approve──▶ Approved   (owner; also appends requester to quest.invited_avatar_ids)
Pending ──reject───▶ Rejected   (owner)
Pending ──withdraw─▶ Withdrawn  (requester)
```

`Approved` / `Rejected` / `Withdrawn` are **terminal and immutable** — a
transition off a terminal state is rejected by the manager. `decided_at` +
`decided_by_avatar_id` are stamped on the terminal transition (owner for
approve/reject; requester for withdraw).

### Idempotency invariant

At most ONE non-terminal (`Pending`) request per `(quest_id,
requester_avatar_id)`. A re-request while a `Pending` one exists returns the
existing row (`GetPendingForQuestAndRequesterAsync`); a re-request after a
terminal state opens a fresh `Pending`. Owner or already-invited avatars are
rejected at the API — no request needed.

### Revoked-mid-run decision

`RunAccess`/invite checks gate **new** run starts + forks. An in-flight run
whose runner later loses their invitation may finish and fork (the fork
inherits the run's own ownership); only fresh starts are gated. This keeps the
gate a start-time check rather than a per-step re-authorization.
