---
type: implementation-checkpoint
status: source-review-in-progress
owner: lane-B
---

# Hold adapter and recovery client checkpoint

Root independently accepted the bounded quartet adapter and ordered request
reconciliation sources after explicit-network and named-predecessor corrections.
This is not activation or runtime acceptance. No tests, builds or schema runs have
occurred; the final integrated sweep remains required.

The Hold resolver now admits frozen commands without signatures, reports pending
until immutable accounting receipts exist, and persists deterministic immutable
config rejection on the original execution version. Explicit network selection
replaces mutable default resolution for opening and human reference lookup. Named
predecessors match run, node, original operation ID and receipt outcome identity.
Tests cover rejection replay/CAS loss, missing authority, pending/accounting proof,
changed default network and foreign predecessor/receipt refusal.

The SDK adds complete Hold command and release-mandate contract methods in the
isolated hold-commands module. The /holds panel discovers server commands, displays
independent transfer/acceptance/mandate costs, saves requests before HTTP, retains
them after response loss, and archives only exact successful or reconciled
Applied/Obsolete outcomes. It can refresh or withdraw unaccepted opening disclosures
while preserving original pending request audits. Component scope changes discard
late responses. SDK/UI fixtures are authored, unrun, and queued for source review.

Generic saved transfer, acceptance, replacement and cancellation reconciliation
reads the command version before the immutable request audit. Same-version
absence is Unresolved; higher-version absence proves that the old CAS cannot commit.
The shared consent identity helper preserves previous canonical field casing.

## Required next implementation

1. Retire legacy HoldController/PlatformHoldService/HoldSweepWorker effects and
   migrate their callers. Exchange/reversal source mapping is independently
   accepted in round3-hold-economic-cutover.md; it requires true pre-effect source
   admission, signed-group journaling, atomic accounting and partial attribution.
2. Complete direct admission and release-mandate management UI, then reviewed DI
   and quartet gate activation with migrated fixtures.
3. Implement Ethereum/Solana custody quote and acceptance parity against the
   existing provider interfaces, coordinated with the protocol lanes.
4. Finish changed-file pruning and independent review before the integrated sweep.

The first source-specific ExchangeParticipantAuthority factory/fence is authored
but unregistered. It retains actual tenant API-key authority separately from the
participant watermark, wallet and selected explicit swap grant. New real-store
fixtures verify actual distinct principals and failure after either principal,
grant, expiry or wallet changes. The owning source transactions and overlapping
first-egress/revocation tests still need implementation. These are work items,
not deferred.md entries.
