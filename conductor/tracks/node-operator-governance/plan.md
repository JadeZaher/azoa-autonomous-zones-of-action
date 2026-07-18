---
type: plan
track: node-operator-governance
created: 2026-07-10
status: in_progress
horizon: alpha-partial
depends_on:
  - avatar-dapp-rbac
  - final-hardening-cutover
related:
  - federation-v2
  - fiat-stripe-bridge
  - data-engine-decision
---

# Plan: node-operator-governance

Two phases, split on the federation-v2 activation gate. Phase 1 is the alpha-cheap
sovereign-node slice (scope + local fee/parameter config). Phase 2 is everything that
needs peers to exist, and is hard-gated on federation-v2.

## Phase 1 — Sovereign-node governance  *(alpha-cheap; may land during alpha hardening)*

The value-standalone slice: a single sovereign node can charge for economic actions and
declare its own economy knobs. Needs NO federation substrate.

### 1.1 The scope
- Add `AzoaScopes.NodeGovern = "node:govern"`.
- Add it to `ApiKeyForbiddenScopes` (JWT-only, stripped at API-key claim-emit time — same
  discipline as `Operator`).
- Confirm it is ABSENT from `SelfIssuableScopes` and `IssuableCapabilityScopes`.
- Add a `NodeGovern` authorization policy (mirrors the `DappManage`/`Operator` policy shape).
- JWT issuance stamps `node:govern` onto the node-operator identity (rides the same
  final-hardening "operator:admin JWT-issuer scope wiring" seam; on a sovereign node the
  operator identity implicitly carries both).
- **AC:** a key CSV containing `node:govern` never satisfies the policy; only a JWT identity
  with the claim reaches the governance surfaces.

### 1.2 Fee schedules (local, on-chain-settling)
- `NodeFeeSchedule` config record (node-scoped, versioned): per-operation-type
  `{ flatBaseUnits, bps }` for Mint / Transfer / Swap / QuestComplete / FederationPublish.
  Modeled on the fiat-stripe-bridge `TOKENS_PER_CENT` operator-authoritative-rate pattern
  (server-side, never client-supplied).
- Fee application seam in the value path: at broadcast time the computed fee settles on-chain
  to a configured `NodeTreasuryWallet` (second chain op) OR, on the fiat-mint path, reduces the
  minted quantity / mints a treasury share inside `AllocationManager.AllocateAsync` BEFORE the
  typed op is built — and inside the SAME idempotency claim (no double-charge on redelivery).
- Immutable `node_fee_audit` trail; changes non-retroactive to in-flight claims.
- **AC:** setting a schedule then minting/transferring applies the fee on-chain; redelivery
  never double-charges; a fee that can't settle on-chain fails the action closed (never a
  phantom off-chain balance).

### 1.3 Governance parameters (local knobs only)
- `NodeGovernanceParameters` config record: chain-allowlist (over the provider set) +
  asset-type-allowlist (over the `HolonType`/`AssetType` vocabulary).
- Enforcement: an action targeting a disallowed chain / asset-type is rejected fail-closed.
- **AC:** disallowed chain/asset-type action rejected; allowlist edit is audited.

Status 2026-07-11: the reusable enforcement primitive has landed as dynamic
`NodeGovernanceParameters` allowlists over chains and asset types, governed by
the JWT-only `NodeGovern` policy. It is wired into allocation, fungible-token
launch, and Holon asset-type writes and fails before idempotency claims or
value/store side effects. The `node_governance_parameters:local` row and
append-only `node_governance_audit` rows are written together in one SurrealDB
transaction.

The fee-schedule slice now has a required fail-closed manager, versioned
schedule plus append-only audit, value-idempotent retries, expected-version CAS,
and allocation Mint netting inside the won allocation claim. Transfer quotes are
fail-closed when nonzero until node-treasury settlement exists. Gross, fee, net,
and schedule version are persisted into the operation/replay payload; crash
reconciliation restores the complete allocation result. Swap, QuestComplete,
and FederationPublish consumers remain open Phase 1 work; their configured
entries must not be called enforced until those value paths quote and settle
them. Direct and quest NFT Mint now fail closed whenever the Mint entry is
unavailable, malformed, or nonzero; allocation Mint alone uses its server-held,
version-pinned quote to mint the net amount.

The remaining consumers cannot share Allocation Mint's simple netting rule.
Raw NFT routes currently persist intent/ownership before broadcast, Swap returns
a client-signed transaction, QuestComplete may be driven by a durable workflow,
and FederationPublish does not yet have a live cross-node value path. Before
enabling any nonzero entry, Phase 1 therefore requires a settlement boundary
with all of the following:

- chain-scoped treasury destinations validated against the target provider;
- a durable `node_fee_settlement` record keyed by the parent idempotency claim,
  operation kind, schedule version, asset, gross/fee/net amounts, and both
  effect states;
- chain-adapter support for one atomic client-signed group where the chain
  supports it, or independently observable/reconcilable primary and treasury
  transactions where it does not;
- recovery that never charges twice and never reports the primary action
  complete while a required fee is only a phantom off-chain receivable;
- manager refactors that win the settlement claim before raw NFT ownership or
  quest/federation publication side effects.

Until those invariants exist, nonzero Transfer and every other unsettled
consumer must continue to fail closed. Local quest definition publication is
not `FederationPublish` and must not be charged under that schedule entry.

Settlement prerequisite audit (2026-07-11) found that submitted operations in
`PendingConfirmation` were omitted from the persisted status enum and recovery
scan, causing them to round-trip as `Unknown`. The enum/schema/reconciliation
closure is now repaired and covered by store round-trip plus chain-confirmation
tests. Ownership reservation/finalization and deterministic atomic submission
remain required before nonzero Transfer activation.

The ownership prerequisite now has a SurrealDB single-winner primitive:
`TryReserveNftTransferAsync` conditionally reserves only an NFT still owned by
the expected source, `FinalizeReservedNftTransferAsync` moves ownership only for
that settlement key, and replay recognizes the recorded finalization key.
Provider submission and reconciliation are not wired to it yet, so nonzero
Transfer remains fail-closed.

Treasury routing policy is now a separate versioned surface rather than a field
inside fee pricing. `NodeTreasuryDestination` is scoped by canonical
chain/network, validated against the selected provider on writes and reads, and
updated with an immutable audit row under one expected-version CAS transaction.
Identical retries are value-idempotent. This closes the configuration
prerequisite only: no fee consumer may use a destination until a durable
settlement claim pins its address and version, and nonzero Transfer therefore
remains fail-closed.

Multi-network provider lifetime is now an isolated factory boundary. Providers
are registered as creators rather than singleton instances; the factory uses a
canonical chain/network key and exactly-once lazy initialization to cache an
independent mutable provider per binding. Focused real-Solana concurrent
resolution proves a Devnet binding stays Devnet after Testnet is created. This
closes the provider-instance prerequisite only: settlement remains disabled
pending the separate atomic-claim, effect-submission, reconciliation, and
consumer-wiring requirements below.

Settlement foundation (2026-07-12): `NodeFeeSettlement` now supplies an inert,
DBML/POCO-first durable intent vocabulary. Its deterministic id is derived from
the parent idempotency key and fee operation; the row pins chain/network, asset,
gross/fee/net amounts, fee-schedule version, treasury address/version, and the
independent primary/fee effect states. `NodeFeeSettlementManager` is intentionally
unregistered: it can only prepare an immutable row and has no controller, worker,
provider submission, or reconciliation caller. This is not activation evidence.
Amounts are schema-validated canonical positive `ulong` base-unit strings and
all version counters are nonnegative. A create replay is normalized only after
the typed store confirms the deterministic record id exists; unconfirmed
statement/schema failures continue to surface rather than being guessed from an
error message.
Before any nonzero consumer uses it, row creation must join the won parent claim
atomically, lifecycle updates need single-winner persistent transitions, and
live-SurrealDB recovery tests must prove no duplicate charge or false completion.

Atomic admission closure (2026-07-13): preparation now creates the parent
`idempotency_key_store` claim and immutable `node_fee_settlement` record inside
one bounded, parameterized SurrealDB transaction, or returns the existing pair.
The new parent remains `InProgress` with neither a cached result nor error; no
terminal parent outcome is recorded until a future terminal settlement protocol
can atomically prove the settlement terminal. A partial historical pair, parent
key/operation mismatch, or terminal parent paired with a non-terminal settlement
fails closed. This replaces the previous separate settlement read/create race,
but does not wire a provider, worker, or fee consumer. The required raw
multi-table transaction has a 2026-08-31 SurrealForge typed-builder waiver and
must be replaced when that package can express conditional cross-table
admission.

Algorand atomic-adapter prerequisite (2026-07-18): `IAtomicTransferGroupModule`
now builds a two-leg same-ASA `TxGroup`, requires the shared sender to match the
resolved signing key (rekey and multisig are unsupported), resolves both
signatures in one custody callback, and sends the resulting envelopes in one
Algod batch. It returns both transaction ids as `PendingConfirmation` unless
both leg observations confirm; only a 404 is interpreted as unseen/pending, so
other observation failures remain errors. This is provider capability only: it
is not wired into a fee consumer, DI path, or recovery worker, so nonzero
Transfer remains fail-closed pending durable settlement lifecycle and
group-level reconciliation activation.

Admission-boundary hardening (2026-07-13): there is no longer an unpaired public
settlement create seam. `AdmitAsync` accepts only a canonical fresh `Prepared`
intent: both effects must be `NotStarted`; operation/transaction identifiers,
leases, reconciliation fields, and terminal lifecycle state are rejected; and
attempt/state versions must be zero. Its raw `CONTENT` write is an explicit
allowlist, so rejected record-link or effect-bearing input cannot reach
SurrealDB. Parent-key trimming is shared by hash and record-id derivation.
An existing ordinary outer idempotency claim with the same parent key returns a
fail-closed conflict and remains untouched; it is not silently reclassified as
a settlement pair. Replay also requires parent and settlement terminality to
agree in both directions. Focused manager and live-Surreal tests cover this
collision, direct effect-bearing rejection, and paired admission without
activating a provider or any fee consumer.

Focused verification (2026-07-13): 16 `NodeFeeSettlementManagerTests` and 7
live-Surreal `SurrealNodeFeeSettlementStoreTests` pass. The live slice includes
two concurrent admissions of a colon-bearing parent key (one create, one exact
replay, one durable `InProgress` parent and one settlement) and a deliberately
invalid settlement write that rolls back its otherwise-new parent claim. These
tests prove the admission boundary only; they are not evidence of chain-effect
execution, reconciliation, or a nonzero Transfer activation.

Recovery seam (2026-07-13): the inert settlement vocabulary now has bounded
durability mechanics without a value-path caller. Every due candidate
or expired lease is re-claimed by a `state_version` plus lease/due CAS; a fresh
opaque lease token, expiry, attempt count, and retry schedule make a crashed
worker reclaimable. The only lease-guarded transition currently releases a
won claim into `AwaitingReconciliation` with an explicit reason. The explicit
`NodeFeeSettlementRecoveryWorker` has no provider dependency and is not
registered as a hosted service, so it cannot submit or confirm either economic
effect. This is recovery safety infrastructure, not settlement activation:
atomic parent-claim creation, effect hand-offs, provider submission,
chain-derived reconciliation, live-Surreal concurrency/recovery proof, and
consumer wiring remain acceptance blockers.

Independent persistence review closure (2026-07-13): the frozen economic
decision is now schema-enforced, not merely manager-checked. SurrealDB permits
the initial `CREATE` but marks the parent-key hash, operation, chain/network,
asset, gross/fee/net amounts, schedule version, and treasury address/version
`READONLY` afterwards. Focused live-Surreal tests cover deterministic concurrent
create normalization, divergent manager preparation, and stale lease rejection
after an expired lease has been reclaimed. This remains inert lifecycle safety,
not provider or consumer activation.

Paired terminal protocol (2026-07-13): an exact recovery lease can now record a
mixed observation containing `Unknown`/`Failed` effects into
`AwaitingReconciliation`, which leaves its parent idempotency claim `InProgress`.
A separate, bounded multi-table CAS can
accept two distinct confirmed effect references and atomically set the
settlement `Settled` plus its matching parent `Completed`, including the replay
payload. Stale leases, parent/key mismatches, illegal proofs, and reverse
terminal transitions leave both rows unchanged. This is a persistence protocol
and live-Surreal test seam, not provider/group submission, a hosted worker, NFT
ownership, a controller, or nonzero Transfer-fee activation. Its raw
transaction is covered by the existing SurrealForge conditional cross-table
mutation waiver, expiring 2026-08-31.

Direct-transfer bypass closure (2026-07-13): `NftManager.TransferAsync` now
reads the effective Transfer schedule before it reads or mutates NFT ownership
or creates an operation. Any nonzero flat or bps entry, unavailable schedule, or
malformed schedule rejects the direct route while no settlement executor exists.
This is a fail-closed containment measure only; it does not reserve ownership,
create a settlement, submit an atomic group, or make nonzero Transfer fees
active.

Direct-mint containment and NFT governance closure (2026-07-17): the public
and quest `NftManager.MintAsync` path now reads the Mint schedule and rejects
unavailable, malformed, or nonzero fees before KYC, Holon persistence, or
operation creation. Allocation persists NFT metadata directly inside its won
claim after a validated, version-pinned quote; it does not expose a bypass
method or create a second pending operation. Mint, transfer, and burn now all
apply the node chain/asset guard before their writes. This is containment, not
settlement activation: no direct NFT route may charge or claim a nonzero fee yet.

Typed-query waiver (expires 2026-08-31): the consumed SurrealForge package
cannot currently represent the recovery scan's contract-cased enum plus
due-or-expired lease predicate, the lease-guarded multi-field effect CASs, or
the parent/settlement cross-table transactions. Those bounded parameterized
statements are marked `raw:` and covered by focused live-Surreal
claim/reclaim/token-guard/paired-terminal tests. Replace them with the typed
package surface before this track can close; the waiver is not permission to add
ordinary CRUD raw queries.

Settlement-reference immutability and SurrealDB 3.x terminalization correction
(2026-07-13): confirmed primary or treasury transaction references are now
monotonic. A nonterminal reconciliation cannot downgrade or replace a confirmed
reference, and paired terminalization rejects a proof that differs from an
already observed confirmation before it can complete the parent. The write-side
lease CAS repeats those predicates, so the read-side fast rejection is not the
only guard. Live SurrealDB also proved that `UPDATE ONLY ... RETURN AFTER` is
already a single object inside a transaction; calling `.first()` aborted the
entire paired-terminal transaction. The protocol now uses that object directly.
Colon-bearing transaction references use the shared temporary
`SurrealScalarString` binding primitive so SurrealDB cannot reinterpret a
record-shaped string as a link. Focused tests cover rejected replacement,
successful exact proof, stale lease, nonterminal reconciliation, and parent
completion. This hardens inert persistence only; it does not authorize a
provider, worker, or nonzero fee consumer.

Accepted atomic-group receipt (2026-07-18): an accepted two-leg provider result
now carries the chain-native group id and can be recorded once under the exact
live `node_fee_settlement` lease. The immutable `node_fee_atomic_group` receipt
pins request identity, source/primary recipient, two transaction ids, and
submission state; the same transaction moves both settlement effects to
`Submitted`, releases the lease, and schedules immediate reconciliation. Exact
replay returns the existing receipt while divergent evidence and stale leases
fail closed. This remains inert evidence only: there is no provider caller,
observer, worker, terminalization change, or nonzero fee-consumer activation.
The pre-receipt broadcast crash window is deliberately still ambiguous and must
never cause automatic re-broadcast.

Receipt binding hardening (2026-07-18): atomic-capable settlement admission may
precommit the canonical `AtomicTransferGroupRequest.GroupIdentity`; receipt
recording now requires that immutable value and repeats it in the lease CAS.
This binds the otherwise-unrepresented source, primary recipient, and signing
context to the durable decision. Ordinary non-atomic settlements remain
compatible with no precommit, but must fail closed if someone later attempts to
record an atomic receipt. The receipt transaction remains one HTTP request via
`SurrealQuery.Combine`: the package rejects semicolon-joined multi-statement
`.Of` bodies, so the six parameterized fragments are one explicitly budgeted raw
waiver, not six independent escape hatches.

Read-only Algorand group observation (2026-07-18):
`IAtomicTransferGroupObservationModule` independently classifies a previously
accepted two-leg group without persistence, signing, broadcasting, settlement
mutation, or terminalization. Before it makes a request, the adapter requires
canonical Algorand transaction and group identifiers plus a positive ASA id;
Indexer confirmation must match the submitted id/group, sender, ASA transfer,
recipient, amount, and the absence of close, clawback, and rekey fields on both
legs in one positive round. Indexer absence may consult Algod only to classify
pending, unseen, or pool-rejected evidence; it cannot confirm a transfer.
Contradictory confirmation dominates a pool rejection. This is a reconciliation
prerequisite only: no observer is wired into receipt recording, a hosted worker,
or a nonzero fee consumer.

Receipt-driven reconciliation (2026-07-18): an explicitly invoked scoped
`NodeFeeSettlementAtomicGroupReconciler` now atomically claims only a due
settlement that already has its deterministic immutable receipt, leaving
ordinary `Prepared`/non-atomic/no-receipt rows unchanged, before reading it.
It reconstructs only
secret-free observation facts from the pinned settlement plus receipt (chain and
network, asset, source/destinations, amounts, group and transaction ids); it
does not recover a signing context or raw parent idempotency key. Before any
provider call it requires the canonical settlement link, precommitted group
identity, receipt transaction ids equal to the settlement's submitted refs,
accepted receipt state, and positive `gross = fee + net` economics. A missing
receipt cannot win the receipt-gated claim and leaves its settlement unchanged;
structurally invalid evidence only releases the receipt-bearing lease
nonterminally. A typed receipt-read integrity/store error stops with the won
lease to expire, without provider observation, terminalization, or a further
settlement mutation, rather than being misclassified as absence. The exact persisted
provider/network must expose the read-only observation capability. Only both
exact receipt legs confirmed at one positive round terminalize through the
existing paired CAS, using the durable parent SHA-256 record id and a deterministic
secret-free replay payload; every other outcome remains nonterminal. DI makes
this scoped seam resolvable but it has no hosted worker, controller, submission,
receipt writer, or fee-consumer caller, so it is not nonzero Transfer activation.

CI compatibility correction (2026-07-18): the paired terminalization's optional
parent-key guard uses an explicit boolean comparison rather than `NOT` applied
to a bound parameter. SurrealDB 3.1.4 rejects the latter form during parsing;
the replacement preserves both hash-only reconciliation terminalization and the
legacy raw-key equality guard inside the same atomic settlement/parent CAS.

Verification evidence (2026-07-11): the API project builds with zero errors;
the regenerated decorated-POCO goldens include treasury tables, ownership
reservation fields/index, and `PendingConfirmation`; 1,422 unit tests pass with
one intentional skip; and the 42-test focused live-SurrealDB slice passes with
no failures. That slice covers governance routes, treasury update+audit CAS and
concurrency, status persistence, and NFT reservation competition/replay. The
track remains `in_progress`: provider network-instance isolation, the durable
`node_fee_settlement` state machine, atomic submission, reconciliation, and the
remaining fee consumers are still acceptance blockers.

### Operational evidence promotion (2026-07-17)

The tracked manual `Promote attested conformance image` workflow accepts only
the current `main` commit and the matching successful `CI` push run. It verifies
the downloaded evidence bundle's GitHub attestation, exact archive shape,
metadata identity/freshness, and every TRX hash before publishing a
SHA-addressable GHCR image. A separate production Dockerfile embeds that
verified bundle root-owned and read-only while preserving runtime write access
only under `/app/data`; it also records the source revision in the assembly and
OCI image label. This is a release-artifact boundary, not deployment activation:
repository administrators must protect the `conformance-promotion` environment,
and Railway must later be moved manually to the emitted immutable digest with a
durable volume and matching runtime conformance settings.

### 1.4 Interop-boundary invariant (documented + tested even pre-federation)
- Assert `node:govern` is node-local: a local rule governs only local-settling actions; no
  local rule reaches a peer. This is a test + doc even in Phase 1 so the boundary is fixed
  before Phase 2 builds on it.

**Phase 1 exit:** a sovereign node charges configurable fees, enforces chain/asset allowlists,
audits every governance write, and the node-local invariant is pinned. No peers required.

## Phase 2 — Federated governance  *(HARD-GATED on federation-v2 activation gate)*

Do NOT start until federation-v2 reaches its own gate (>=3 operators + >=1 real cross-node
use case). A peer policy with no peers is inert.

### 2.1 Federation rules
- `FederationHolonPolicy` (per-type/tag/list allow-deny) consulted by federation-v2's L1
  "submit a holon for federation" before publishing a `HolonReference`. Default private/local.
- `FederationPeerPolicy` (peer allowlist/denylist) — which peers this node resolves from.
- Per-peer `ConformanceThreshold` — minimum G-suite gates a peer must publish (L0
  `CertificationProof`) before this node collaborates; below-threshold peer rejected fail-closed.

### 2.2 Ecosystem incentives (bounded parameters)
- `NodeIncentiveParameters`: quest-completion bonus, referral incentive, staking-style knob —
  read by the existing Tier-2 economic nodes (Grant/Emit/Transfer). NO general rules DSL.

### 2.3 Cross-node interop enforcement
- Enforce the crux: a cross-node effect requires conformance (core protocol: G-suite +
  versioned HolonType + agent-signing format) AND mutual opt-in (A publishing does not obligate
  B; B's `FederationPeerPolicy` decides). Test both directions.

### 2.4 Membership/onboarding rules (cross-node reach)
- Node-local join policy (open / invite / KYC-gated) composing the existing KYC gate +
  quest-invitations model, extended to the federated case.

**Phase 2 exit:** an operator sets enforced federation rules + peer policies + bounded
incentive parameters; cross-node collaboration is conformance-required and opt-in-only; no
local rule is ever imposed on a peer.

## Prerequisites carried from related tracks
- avatar-dapp-rbac scope-manager + policy model must be shipped (Phase 1 reuses it).
- final-hardening value engine / `AllocationManager` / `HolonType` registry (Phase 1 fee seam).
- federation-v2 P0 (gate-result serializer), P1 (versioned HolonType vocab), and its L0/L1
  client must exist before Phase 2 (the conformance threshold + holon-reference policy read them).
