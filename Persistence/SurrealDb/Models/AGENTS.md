# Persistence/SurrealDb/Models — decorated POCO notes

`ethereum_dex_allowance` retains exclusive ERC20 allowance ownership until a
finalized swap or separately authorized zero-allowance cleanup. Its versioned
resource identity includes physical network, owner, token and executor; there is
no time-based lease. Emit its schema during the final integrated generation.

## DEX immutable quotes

`dex_quote` stores immutable canonical public protocol terms and their hash, scoped
to actor/tenant. Its nullable consumption link is changed only by atomic swap
admission, not quote replay. No custody context, key or signed wire belongs in the
quote. New schema emission is held for the final integrated sweep.

`swap_operation` stores the actor/tenant-scoped original request and immutable
quote/intent hashes, plus versioned orchestration state. A cancelled record can
exist before quote consumption to fence delayed first admission. Private intent
and action correlation fields never replace transport authorization. Stage
preparation and first-send authorization remain separate required transactions;
the admission row alone grants neither signing nor broadcast.

## WTT protected stage keys

`wtt_stage_key` stores authenticated ciphertext and the immutable public key/binding, never a seed or custody secret. Its content-hash record identity is unique per operation/role/generation and authority scope. Encryption-version changes are exact-envelope CAS updates. The table has no generic user read or delete surface; retention requires the old decrypt version until rewrapped. Generate its schema with the integrated schema sweep before enabling WTT.

Design rationale / cross-cutting invariants for the hand-authored SurrealDB
POCOs in this directory. Code carries terse one-line doc-comments; the WHY
lives here. Goldens under `Generated/Schemas/*.surql` are GENERATED — never
hand-edit; regenerate via the byte-equivalence test with
`AZOA_REGENERATE_GOLDENS=1` (see `AttributePocoByteEquivalenceTests`).

Each `*Kind` enum inside a POCO MIRRORS its domain enum
(`Models/Actions/ActionEnums.cs`); the `[Inside(...)]` allow-list on the
string-enum field MUST enumerate exactly those names, or a SCHEMAFULL ASSERT
rejects a legitimate write.

`OperationLog.StatusKind` and `SwapState.StatusKind` include
`PendingConfirmation`. It represents a submitted transaction with a persisted
hash whose chain verdict is not yet terminal. Mapping it to `Unknown` is a data
loss bug because reconciliation selects by the closed status set.

## §kyc-identity-binding

`kyc_identity` binds a real person to their users. It is **node-operator-only**
and is structurally excluded from every generic consumer read surface:
`SearchableEntityType` is a closed flags enum with no identity member,
`HolonQueryRequest` exposes no table selector at all, and the MCP
`user_scoped_query` tool's table allow-list does not contain it. That exclusion is
asserted by `KycIdentityReadSurfaceExclusionTests`, not assumed — this repo has
leaked cross-tenant data through a generic read surface before, and the
person→users link is exactly the field whose exposure would reveal that a subject
also exists under another tenant.

**What is stored.** A salted HMAC of `document_number` + `issuing_state`, plus only
`first_name`, `last_name`, `birthday`, `citizenship`. Address, `personal_number`,
and every media/biometric artefact are never persisted — the DTO that carries a
provider result (`HostedSessionSubjectAttributes`) has no property for them, so the
omission is structural rather than a filtering step someone can forget.

**D23 — per-tenant salt.** `person_key` is HMAC'd under a salt derived by HKDF from
one root secret and the tenant scope, so one human resolves to an *unlinkable* key
in different tenants. Uniqueness is `(tenant_scope, person_key)` and there is
deliberately **no** unique index on `person_key` alone: a global constraint would
reject a second tenant's legitimate verification of the same human and, by failing,
would itself disclose that the human exists elsewhere. `global_person_key` is stored
and indexed but NOT unique — cross-tenant resolution is an explicit per-operation
node-operator opt-in, never an implicit constraint.

**D25 — erasure preserves the key.** `EraseAsync` uses an explicit `SET` clause that
tombstones only the four personal fields and stamps `erased_at`/`erasure_reason`.
`person_key`, `global_person_key`, `user_ids`, provenance, and `created_date` are
untouched, so GDPR Art. 17 erasure and KYC record-keeping both hold: every readable
personal attribute is gone while uniqueness, the user binding, and the audit trail
survive. Erasure is idempotent. `retention_expires_at` drives the automatic sweep.

**At-rest encryption posture.** These rows are pseudonymous by construction: the
person keys are keyed hashes whose preimage AZOA does not retain, so a database
compromise alone does not reveal a document number — an attacker additionally needs
`Kyc__Identity__PersonKeySecret`, which lives only in the host secret store and never
in the repo or the database. The four retained personal fields are stored in
plaintext and rely on the deployment's storage-level encryption at rest; AZOA adds no
field-level encryption for them today. That is a deliberate, reviewable position, not
an oversight: field-level encryption without a key-management and rotation story
would give the appearance of protection while making erasure and audit harder.
Rotating the root secret re-pseudonymises every binding and is therefore a migration,
not a config change — `person_key_version` exists to make such a rotation auditable.

`kyc_screening_result`, `kyc_screening_monitor`, and `kyc_screening_poll_state`
carry the same node-operator-only exclusion. `hosted_session` is the generic
(non-KYC) durable admission record; see `Services/HostedSessions/AGENTS.md`.

## §person-operation-claim

`person_operation_claim` is the D3 per-operation Sybil ledger: one row per
`(uniqueness_scope, operation_key, person_key)` triple, unique on exactly that.
It is **node-operator-only** and carries the same structural exclusion as
`kyc_identity` — absent from `SearchableEntityType`, from `HolonQueryRequest`
(which exposes no table selector at all), and from the MCP `user_scoped_query`
allow-list. `PersonUniquenessGateTests` asserts all three for BOTH tables,
because a perfect denial message is worthless if a consumer can list the table
and read a person key it did not create.

**It stores a person KEY and nothing else about the human.** No first/last name,
birthday, citizenship, or document field may ever be added: one joinable
attribute here re-identifies the whole pseudonymous claim trail.

`uniqueness_scope` is a tenant `Guid('N')` by default and the node-global
sentinel only on an explicit per-operation opt-in, mirroring `kyc_identity`'s
`tenant_scope`. The scope also chooses which key is stored — `person_key` for a
tenant claim, `global_person_key` for a node-global one — so the two domains can
never collide inside one row.

`Id` is a deterministic length-prefixed SHA-256 over the triple, so an upsert
converges on one row and the record id and the unique index always agree. The
length prefixes matter: a bare concatenation lets `("a","bc")` and `("ab","c")`
collide, which here would silently merge two operations' uniqueness domains.
The recipe mirrors `KycPersonKeyDeriver.RecordId` but is implemented locally so a
persistence POCO takes no dependency on a service.

`released_at` is the compensation stamp. A released claim no longer blocks, so a
runner whose side effect failed is not locked out by a claim that bought nothing;
the row survives as the audit trail of the attempt. The non-unique
`(uniqueness_scope, operation_key)` index exists for operator audit only.

`kyc_identity` gains `kyc_identity_scope_users` on `(tenant_scope, user_ids)` for
the same feature: SurrealDB indexes array fields elementwise, so that index serves
the `$user INSIDE user_ids` reverse lookup the gate needs to turn an acting user
into a person key.

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

`NodeFeeAtomicGroup` is the one-to-one immutable receipt for an accepted
two-leg chain group. Its deterministic id derives only from the settlement id;
the unique settlement link is the schema backstop. It records the request
identity, chain-native group id, source/recipient, two distinct transaction
ids, and accepted submission state. It is evidence of submission, not a chain
observation or parent terminalization: reconciliation must still independently
verify both legs before a settlement can complete.

`NodeFeeSettlement.ExpectedAtomicGroupIdentity` is optional for ordinary inert
settlements but, once supplied, is immutable 64-character lowercase SHA-256
evidence of the exact source, recipients, amounts, signing context, provider
chain/network, and parent-key digest in an `AtomicTransferGroupRequest`. Receipt
recording is fail-closed when it is absent or differs; this prevents a later
lease holder from substituting a different signer or recipient into an otherwise
matching amount decision.

## NFT transfer projection reservation

`Holon.TransferReservationKey`, `TransferTargetUserId`, and
`TransferReservedAt` form the pre-broadcast ownership reservation. The source
owner remains authoritative until chain confirmation. A conditional finalize
moves `UserId`, clears the reservation, and records
`LastTransferSettlementKey` so crash replay is idempotent. These fields are
workflow provenance, not an off-chain asset balance. Reusing a reservation key
with a different target is an idempotency conflict and must never rewrite the
reserved target.

## §action-access-request

action-invitations-approval track. Adds a **run-authorization** dimension to
`ActionDefinition`, orthogonal to `is_public` (discoverability):

- `action.run_access` (`Open` | `InviteOnly`, default `Open`). `Open` is
  today's behavior — anyone who can view may run/fork. `InviteOnly` gates
  run/fork to the owner + `action.invited_user_ids`. Viewing is unaffected:
  a public + InviteOnly action is fully viewable by non-invited users; only
  starting/forking is gated.
- `action.invited_user_ids` (`array<string>` of `user:hex` links) — the
  approved invite set. The owner is ALWAYS implicitly invited and is never
  written into this list.

`action_access_request` is the self-service request/approve flow for an
InviteOnly action.

### State machine (enforced in the manager, not the DB)

```
Pending ──approve──▶ Approved   (owner; also appends requester to action.invited_user_ids)
Pending ──reject───▶ Rejected   (owner)
Pending ──withdraw─▶ Withdrawn  (requester)
```

`Approved` / `Rejected` / `Withdrawn` are **terminal and immutable** — a
transition off a terminal state is rejected by the manager. `decided_at` +
`decided_by_user_id` are stamped on the terminal transition (owner for
approve/reject; requester for withdraw).

### Idempotency invariant

At most ONE non-terminal (`Pending`) request per `(action_id,
requester_user_id)`. A re-request while a `Pending` one exists returns the
existing row (`GetPendingForActionAndRequesterAsync`); a re-request after a
terminal state opens a fresh `Pending`. Owner or already-invited users are
rejected at the API — no request needed.

### Revoked-mid-run decision

`RunAccess`/invite checks gate **new** run starts + forks. An in-flight run
whose runner later loses their invitation may finish and fork (the fork
inherits the run's own ownership); only fresh starts are gated. This keeps the
gate a start-time check rather than a per-step re-authorization.

## §amount-columns

Every persisted base-unit amount is a **string of decimal digits**, never a numeric
column. The domain type is `RailAmount` (a `BigInteger` count plus a `ValueDimension`,
`Core/AGENTS.md` §rail-amount), and D13 widened it precisely because `ulong.MaxValue` is
~18.45 ETH in wei — the old surface could not express an ordinary Ethereum holding. A
numeric column re-imposes that ceiling one layer down and moves the bug rather than
fixing it: SurrealDB's `int` is i64, so `operation_log.amount` as `option<int>` capped
the row at half the range the interface already accepted.

Columns: `operation_log.amount`, `bridge_tx.amount` (string from the start),
`node_fee_settlement`'s base-unit fields.

Read/write goes through `RailAmount.ToBaseUnitString()` and `RailAmount.TryParse`, which
is `NumberStyles.None` — no sign, no separators, no exponent. `SurrealAmountColumn.Parse`
is the shared adapter helper. An **absent** column reads as zero (nothing was recorded);
a **present but unparseable** one throws, because a row that cannot say how much it moved
must not quietly read as having moved nothing. Bridge rows tighten this further: zero is
never a legitimate bridge amount.

## §value-stamp

`operation_log.value_currency_code` / `value_minor_units` / `value_rate` /
`value_rate_as_of` / `value_rate_source` are D16: the fiat value of an operation frozen
at the moment it settled, written by `BlockchainOperationManager` on the same write that
records the terminal outcome.

**Why the rate and its timestamp are stored, not just the figure.** Reconstructing a
historical price after the fact is lossy and expensive, and a computed figure whose
inputs are gone cannot be audited or re-derived. Storing the applied rate, when it was
observed, and where it came from makes the number checkable years later. Scope is USD
only per D15, but the currency code is carried as *data*, so a second currency is a value
change rather than a schema break.

**Absent is not zero.** There is no price oracle in this codebase. `IValueRateSource` is
the seam one plugs into; nothing implements it, `UnpricedValueRateSource` is the default
and it never quotes, so **every row written today is legitimately unstamped**. All five
columns NONE means "never priced". A `value_rate` of `"0"` means a real source really did
quote zero. Those are different facts and the schema keeps them different. A fabricated,
defaulted, or stale-presented-as-current rate on a financial record is worse than an
absent one — this is the same hard invariant that governs the value rails.

A rate source that throws is treated exactly like one that declines: the chain effect has
already happened by stamping time, so failing the operation would turn an oracle outage
into a settled-but-Failed row, and guessing would fabricate a value. The operation
settles unpriced and the failure goes to the log.

Only operations that move one identifiable quantity are stamped — Mint, Burn, Transfer.
Swap and Exchange move two different assets and Deploy/Call move none, so they carry no
single amount to price and stay unstamped rather than stamped with half a truth.
Statuses that mean nothing was broadcast (`Failed`, `AwaitingSignature`, `Unknown`,
`Pending`) are never stamped. A stamp is written once and never recomputed on read.

## KYB current authority

`kyb_verification` uses the tenant owner's Guid as its record key. It is excluded from generic
search/MCP tables; access is through the claims-bound business-verification service. Business
registration identifiers and beneficiary provider session links are server-only. Public DTOs
carry status, timestamps and participant count. Historical hosted-session outcomes remain in
`hosted_session`; the current business projection uses attempt/version/policy CAS. See
`Services/Kyb/AGENTS.md` for the documented retention and verification boundary.
