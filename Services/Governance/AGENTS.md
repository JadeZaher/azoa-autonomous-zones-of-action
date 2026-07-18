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
treasury transfer can settle it on-chain. Swap, quest-completion,
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
no actor/avatar field, internal record id, raw snapshot JSON, or free-form audit
detail. Actor continuity is omitted until a separately configured, node-scoped
HMAC key exists; hashing a low-entropy avatar id without such a key would not be
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
public payload, timestamp, kind, and content hash: no actor/avatar ids, record
ids, raw persisted JSON, or free-form details.

This is not a claim that a database root credential is solved. It has a trusted
first checkpoint and bounded history (default maximum 512 entries), cannot prove
completeness/non-equivocation without an independently retained or witnessed
checkpoint, and stays unavailable until `NodeTransparencyHistory:Enabled` plus
the configured dedicated `NodeConformance` identity are present. Do not use it
to activate egress fees, federation, or value settlement. Transparency routes
remain globally rate-limited but permanently outside any paid-egress policy.
