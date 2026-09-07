# Services/Governance

## node-governance

`NodeGovernanceGuard` is the local node's fail-closed runtime policy gate for
economic-engine generation. Its allowlist semantics are intentionally explicit:

| Value | Meaning |
| --- | --- |
| `null` allowlist | unrestricted dimension |
| empty allowlist | deny every value in that dimension |
| non-empty allowlist | case-insensitive exact membership |

Persistent parameters in `node_governance_parameters` override config once a
row exists; otherwise the guard falls back to `NodeGovernance` appsettings. A
store read error is a security decision failure, so the guard denies instead of
silently falling back.

## node-fees

`NodeFeeScheduleManager` computes `flat + floor(gross * bps / 10_000)` with
`UInt128` intermediates. Flat amounts are unsigned 64-bit base-unit strings and
basis points are limited to `0..10_000`. A quote fails closed when storage is
unavailable, arithmetic overflows, the schedule is invalid, or the fee is at
least the gross amount. The returned schedule version is part of the economic
decision and must be persisted with every consuming operation.

The current settling consumer is allocation Mint, where the fee remains unminted.
Allocation Transfer quotes the schedule but rejects a nonzero fee until a node
treasury transfer can settle it on-chain. Swap, action-completion,
federation-publication, and direct NFT routes remain explicit follow-up consumers;
do not describe their schedule entries as enforced until their value paths quote
and settle them.

`NodeFeeSettlementDraft` is the domain input for the future two-effect settlement
path. It carries only a parent idempotency key, a canonical economic decision,
and the already-validated treasury route; it is neither a user request nor a
chain command. `NodeFeeSettlementRecoveryWorker` runs an explicit, bounded,
clock-injected recovery sweep only: it claims due/stale rows through the manager
and returns them to `AwaitingReconciliation` with a retry time. It has no chain
provider dependency and is deliberately not registered as a hosted service.
No recovery result is a transfer authorization, submission, confirmation, or
fee settlement. Parent-claim atomicity, effect dispatch, and chain-derived
reconciliation remain activation prerequisites.

`RecordAcceptedAtomicGroupAsync` is an unregistered manager seam for durable
post-broadcast evidence. It accepts only a live settlement recovery lease plus
an `IAtomicTransferGroupModule` request/submission pair, delegates the immutable
economic binding and CAS to the store, and leaves the parent InProgress for
chain reconciliation. It does not submit, poll, or terminalize a transfer.
The settlement draft can precommit its request's canonical group identity at
admission. This is optional for non-atomic settlement intents, but mandatory
for receipt recording and immutable thereafter; do not permit a receipt based
only on matching amounts or treasury routing.

`NodeFeeSettlementAtomicGroupReconciler` is a scoped, explicitly invoked
observation seam registered only so an operator-controlled caller can resolve
it. It is not a hosted worker and has no controller or fee-consumer caller.
It atomically claims only a due settlement that already has the deterministic
immutable receipt, leaving ordinary `Prepared`/non-atomic/no-receipt rows
entirely untouched, then
requires its exact settlement link, precommitted group identity, persisted
transaction references, accepted receipt state, and positive pinned economics
before it reconstructs secret-free observation evidence. Signing context and the
raw parent idempotency key are intentionally absent from that evidence. A missing
receipt cannot win the receipt-gated claim and leaves its settlement untouched;
a successfully read but structurally inconsistent receipt is only deferred. A
typed receipt-read integrity/store error stops with the won lease left to expire
and performs no provider call, terminalization, or further reconciliation
mutation, so corruption/infrastructure is not misclassified as missing evidence.
The reconciler resolves the exact persisted chain/network provider and its
observation capability. Only two exact receipt transaction IDs confirmed in one
positive round use the existing paired terminal CAS. That CAS can now take the
persisted parent SHA-256 record id when the raw parent key is unavailable; it
still requires the matching parent record and operation, and never reconstructs
or logs the key. All other observations remain nonterminal and retain a retry.
The deterministic parent replay payload contains settlement/group/transaction
identifiers and confirmation round only.

## node treasury routing

Treasury destinations are separate policy per canonical chain/network. The
destination row and immutable audit entry are committed atomically under an
expected-version CAS; identical retries are value-idempotent and stale differing
writes conflict. Addresses are provider-validated on write and read. Settlement
must pin both the address and routing-policy version, so rotation cannot redirect
an in-flight fee.

## public transparency cursors

`NodeTransparencyManager` is the anonymous read boundary for current governance,
fee, and treasury policy plus their audit histories. Its DTOs deliberately have
no actor/user field, internal record id, raw snapshot JSON, or free-form audit
detail. Actor continuity is omitted until a separately configured, node-scoped
HMAC key exists; hashing a low-entropy user id without such a key would not be
anonymization.

History pages use descending `(occurred_at,id)` keyset order. The internal record
id is carried only inside an ASP.NET Data Protection cursor, which authenticates
and encrypts it before it reaches the wire. Deployments must persist the Data
Protection key ring if cursors must survive a node restart. The one raw read in
`SurrealNodeTransparencyStore` is a temporary, Phase-A-scoped waiver because the
current typed SurrealForge read builder cannot express the composite predicate.
Remove the waiver when that typed predicate lands; it expires before this track
can be completed.

Any environment other than Development/IntegrationTest fails startup without
`DataProtection:KeyRingPath`. That directory
must be a durable shared mount for every API replica and all replicas must use
the same versioned `ApplicationName`; a container-local directory is not enough.
The dev compose stack mounts a named volume at the configured path. Railway and
other multi-instance deployments must mount durable shared storage at
`/app/data/data-protection-keys` (or override `KeyRingPath`).

`ContentSha256` domain-separates and hashes the server's typed policy/items JSON;
it is not a cross-language canonicalization format. It backs a weak semantic HTTP ETag
(the protected cursor ciphertext may legitimately vary while its position does not).

## signed audit checkpoints

`NodeTransparencyHistoryService` is an opt-in, bounded read-only proof surface
at `/api/node-transparency/audit/checkpoint`. It turns the three existing audit
tables into a canonical, redacted, ascending chain and signs the resulting head
with the same dedicated local node-identity key used by L0 conformance — never a
wallet or governance actor credential. The last signed checkpoint is protected
beside that identity key, outside SurrealDB. A subsequent history must preserve
the checkpoint's exact prefix before it may extend it; a rewrite, truncation, or
mutation after a checkpoint fails closed. Public entries contain only a typed
public payload, timestamp, kind, and content hash: no actor/user ids, record
ids, raw persisted JSON, or free-form details.

This is not a claim that a database root credential is solved. It has a trusted
first checkpoint and bounded history (default maximum 512 entries), cannot prove
completeness/non-equivocation without an independently retained or witnessed
checkpoint, and stays unavailable until `NodeTransparencyHistory:Enabled` plus
the configured dedicated `NodeConformance` identity are present. Do not use it
to activate egress fees, federation, or value settlement. Transparency routes
remain globally rate-limited but permanently outside any paid-egress policy.


## consumer-governance

Not the node-operator lane above: `ConsumerGovernanceHolonTypes`,
`GovernanceTallyHash`, and `GovernanceBallotRoll` describe what a TENANT'S
MEMBERS vote on. A proposal is a Holon; a ballot is a child Holon; D20 adds no
tables.

**A proposal is published, a ballot never is.** `HolonManager.CanRead` is
owner-or-public, so a proposal only its author can read is a proposal only its
author can vote on -- the primitive is inert. `GovernanceProposalCreate`
therefore writes the proposal with `IsPublic = true`, and `IsPublic` is reachable
from `HolonCreateModel`/`HolonUpdateModel` ONLY for the asset types on
`HolonManager.PublishableAssetTypes` (today: `GovernanceProposal`). That
allow-list is the whole point: a free `isPublic: true` on `POST /api/holon` is
the cross-tenant read leak this repo has already shipped three times. A ballot is
deliberately NOT publishable, because a public ballot carries `Holon.UserId`
beside the choice and reconstitutes the person-to-choice correlation D20 exists
to prevent. Publishing is refused with an error, never silently downgraded to a
private write.

**A ballot counts only when a live uniqueness claim backs it.** Ballots are
ordinary child holons and `POST /api/holon` writes them, so counting child
holons counts forgeries: one verified user can mint five hundred
`GovernanceVote` children without ever reaching `GovernanceVoteNodeHandler`.
`GovernanceBallotRoll` is the join that makes "one member, one vote" real -- for
each ballot's owner it resolves the person behind them
(`IKycIdentityStore.GetByUserAsync`) and looks up the node-operator-only
`person_operation_claim` row for THIS proposal's uniqueness domain. A ballot
counts iff a live, unreleased claim on that domain is held by that ballot's
owner, and each claim is spent at most once (earliest `castAt` wins, so a
re-tally is stable). Two users of one human share a person key and therefore one
claim row: exactly one of their ballots counts.

The domain comes from the PROPOSAL, in both the vote and the tally: the declared
`uniquenessOperationKey` must equal `UniquenessOperationKeyFor(proposalId)`, and
the scope is parsed with the strict spelled-name parser. The scope value itself
is `KycPersonKeyDeriver.ScopeValue`, the same one the gate claims through, so a
Tenant-scoped proposal counts its own tenant's claims and nobody else's.

`GovernanceBallotRoll` is also the one place allowed an UNSCOPED holon read.
Ballots are private and each is owned by its own voter, so the consumer-scoped
owner-or-public read shows a tally runner only their own ballot and certifies a
count of one (that was the defect). The read is unscoped and the return type is
`GovernanceBallotCount` -- a total and per-option counts -- so identities cannot
leave. Store faults fail the whole tally: a skipped ballot would publish a wrong
count under a correct-looking `tallyHash`.
