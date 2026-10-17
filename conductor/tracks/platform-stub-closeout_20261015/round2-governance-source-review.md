---
type: review
status: source-accepted-runtime-pending
---

# Round 2 governance independent source review

SOURCE_VERDICT: ACCEPTED

Root reviewer acceptance applies to the corrected Round 2 source described below and
in `round2-governance-independent-review.md`. Earlier findings in this document remain
as review history. ActionExecutionFence, policy version/absence locking, shared KYC/AML
evidence fencing, null-input validation, failed command receipts and flat transaction
statement composition have received separate author/reviewer passes. Their source
corrections are accepted. The action lane may now align publication, dispatch and
catalog gates with these implemented handlers and their actual required inputs.

Runtime verdict: NOT RUN. This marker is not production acceptance or a passing-test
claim. Real persistence races, generated schemas, handler/API journeys and the integrated
build/test sweep remain mandatory before final delivery. Any failures reopen acceptance.

Scope: ConsumerGovernanceService and Finalization partial, GovernanceActionResolver,
GovernanceCommandIdentity, SurrealGovernanceStore create/vote/finalization partials,
and persisted proposal/receipt/identity mappings. This is a source review; no runtime,
build, schema, or test execution has occurred.

## Required correction

P1: dedicated governance mutation transactions initially did not fence the originating
action execution or run. A handler suspended after admission could commit a proposal,
vote, or finalization after fork/cancel canceled its node. The deterministic command
receipt prevents duplicates but does not preserve cancellation authority. Parent author
accepted and is adding a common atomic run/node state and frozen-intent fence. Existing
receipt recovery must remain readable without re-authorizing the already committed effect.

## Checked boundaries

- Principal-only election administration supplements TenantAuthority provisioning checks.
- Electorate uses tenant, owned active children, and live Participation grants with refs;
  UserExplicit grants are not membership.
- Vote transaction serializes on proposal, checks DB cutoff and fixed electorate, current
  membership and exact KYC/AML lineage, then writes vote and unique receipt atomically.
- Tally reads persisted ballot eligibility rather than re-running current membership/AML.
- Fixed options, definition/electorate hashes, cutoff, and final receipt/projection are present.
- ApiKey persistence uses array<string> scopes. SQL CONTAINS is exact membership here;
  the domain CSV property does not imply substring SQL authority (an initial suspicion
  was retracted after inspecting the actual persistence POCO).

Remaining review: reread action fence correction, real-Surreal transaction fixtures,
handler fixture migration, changed-file pruning and authoritative finalization race tests.
No gate-lift approval yet.

## Correction reread and policy race

The extracted ActionExecutionFence was independently reread: run/node write fences,
actor/tenant and frozen-input SHA match the accepted contract. Source-only acceptance
for that correction; concurrency runtime evidence remains outstanding.

A second boundary gap was found: proposal creation checked node-governance allowlists
before the transaction without fencing the singleton version/absence. Author accepted.
The action lane authored a shared NodeGovernancePolicyFence and a dedicated policy-lock
row, used by both guarded effects and policy updates; policy author will capture the
version before validation and splice the guard into create. The shared helper and
policy-store integration require independent source review by parent because this
reviewer authored them. An absent-policy then first-policy-insert regression is authored.

## Current-proof concurrency finding

Vote admission currently writes the voter/proposal/participation rows but only selects
KYC submission, selection/profile, identity and latest AML evidence. Their mutation
paths do not share the voter lock. A concurrent AML append is especially significant:
locking the previously selected NO_HIT row cannot fence a newly inserted HIT becoming
latest. Required correction: a shared subject/tenant evidence revision/lock used by
proof-changing writers and vote admission, with deterministic concurrent revocation/HIT
fixtures. Parent author notified; this remains a blocking source finding.

Create config also requires an explicit options-null validation before Select; malformed
JSON must yield typed Invalid rather than NullReferenceException. Reserved governance
claim namespace/ID restrictions were reread and appear consistent at their generic APIs.
