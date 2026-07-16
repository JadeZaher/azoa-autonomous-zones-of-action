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

`OperationLog.StatusKind` and `SwapState.StatusKind` include
`PendingConfirmation`. It represents a submitted transaction with a persisted
hash whose chain verdict is not yet terminal. Mapping it to `Unknown` is a data
loss bug because reconciliation selects by the closed status set.

## node-operator-governance

`NodeGovernanceParameters` is a singleton local policy row keyed by `local`.
For both `AllowedChains` and `AllowedAssetTypes`, `null` means unrestricted and
an empty array means deny all. Preserve that distinction in mappings and tests.

`NodeGovernanceAudit` is append-only. It stores both previous and new allowlists
so an operator can reconstruct exactly what changed without diffing historical
parameter rows. Do not add update/delete store methods for the audit table.

`NodeFeeSchedule` is also a singleton keyed by `local`. Flat fees are unsigned
integer base-unit strings; bps values are `0..10000`. Its `Version` is a CAS
token, not display metadata: schedule replacement and the matching
`NodeFeeAudit` insert happen in one transaction only when the stored version
matches. `NodeFeeAudit` is append-only and stores full previous/new JSON
snapshots so every economic decision version is reconstructable.

Governance and fee singleton `created_at` fields are database-defaulted and
read-only. Their CAS transactions use explicit `SET` clauses that omit the
field, preserving the original creation time on every update.

`NodeTreasuryDestination` is keyed deterministically by canonical chain/network
and also has a unique composite index over those columns. It is routing policy,
not part of `NodeFeeSchedule`: rotating a treasury must not rewrite pricing, and
an in-flight settlement must retain the address and destination version it
claimed. `NodeTreasuryAudit` is append-only; destination CAS and audit creation
belong to one transaction.

`NodeFeeSettlement` is the durable, currently inert boundary for a fee consumer
that needs two observable on-chain effects. Its deterministic record id uses a
hash of the parent idempotency key plus operation; never persist the raw tenant
key. A prepared row freezes chain/network, asset, gross/fee/net amounts,
fee-schedule version, and treasury address/version before either effect can be
submitted. Its three base-unit amounts are canonical, positive unsigned 64-bit
decimal strings (no sign, leading zero, or value above `ulong.MaxValue`), and
the POCO assertions enforce that contract even when a caller bypasses the
manager. The parent-key hash, operation, chain/network, asset, three amounts,
fee-schedule version, and treasury address/version are also `READONLY`:
SurrealDB accepts them during the initial `CREATE`, then prevents an economic
decision rewrite. Fee, treasury, attempt, and state version counters are nonnegative at
the schema boundary. Recovery uses `lease_token`, `lease_expires_at`, and the
CAS `state_version`: a candidate is claimable only when due with no lease or
when a prior lease has expired, and a release needs the exact live
token/version. `next_attempt_at` bounds scans while `reconciliation_reason`
explains why an inert worker left the row non-terminal. `Prepared` does not
authorize a chain action: activation still needs an atomic parent-claim creation
path, reviewed effect hand-offs, provider submission, and chain reconciliation.
Do not model fees as an AZOA off-chain balance.

## NFT transfer projection reservation

`Holon.TransferReservationKey`, `TransferTargetAvatarId`, and
`TransferReservedAt` form the pre-broadcast ownership reservation. The source
owner remains authoritative until chain confirmation. A conditional finalize
moves `AvatarId`, clears the reservation, and records
`LastTransferSettlementKey` so crash replay is idempotent. These fields are
workflow provenance, not an off-chain asset balance. Reusing a reservation key
with a different target is an idempotency conflict and must never rewrite the
reserved target.

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
