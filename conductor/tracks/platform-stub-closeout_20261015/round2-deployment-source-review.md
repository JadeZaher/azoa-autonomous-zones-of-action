---
type: Review
title: Deployment source review during staged implementation
status: Corrections required; package still being authored
---

Coordinator inspected the first deployment compiler/service/store implementation before provider partials were complete. This is an early review, not a final package verdict.

1. ObserveAsync currently accepts any PreparedJson, and AdvanceAsync with authorizeNewStage=false can observe Pending then set Submitted and send. Prepared appears in ListPending. This authorizes first egress from mere preparation. Require an interactive Prepared-to-Submitted authorization CAS and restrict observer eligibility to durable authorized sends. Observation errors cannot convert unauthorised Prepared into a worker-sendable NeedsOperator record. Add crash-before-authorization evidence.
2. Compose copies edge conditions unchanged and removes all dependencies targeting any included source action. Preserve original condition/data/control semantics by remapping occurrence-specific bindings and resolving internal dependencies, or reject ambiguous composition explicitly. Repeated source occurrences cannot justify dropping dependencies. Add equivalence and ambiguity regressions.
3. NativeBudget's QuoteId is only checked for nonempty text and WithinBudget runs after PrepareNativeStage signs. Provide an authoritative quote/intent digest before signing or nonce reservation, bind cumulative multi-stage fees/rent/value limits, and verify prepared wire against it. MaximumValue requires actual enforcement/accounting where stages transfer value. Returned receipts must distinguish spent and reserved costs. Provider work is still being authored; these are required completion checks.

The compiler also imports both System.Text and AZOA.WebAPI.Helpers; qualify the BCL Encoding use to avoid ambiguous naming. All findings were sent to the author during the staged wave. No build or test has run.

## Bounded follow-up

The author added a persisted SubmissionAuthorized flag, worker eligibility guard, interactive first-send CAS, server-persisted intent-bound quote and reserved-value accounting. The compiler now carries occurrence-specific internal dependency requirements and authors tests for repeated occurrence ambiguity and conditional bindings. These source changes address the initial design direction; actual provider enforcement and runtime integration remain in progress.

Two further corrections are required. First, initial authorization of previously prepared wire must revalidate live scoped custody consent and wallet authority, not only value readiness and chain policy; permission may have been revoked since preparation. Already-authorized replay remains a distinct recovery operation. Second, deployment listing currently limits the actor's newest 100 records before tenant filtering. This can hide all operations for another tenant; apply tenant scope before limit and provide stable pagination/discovery.

Bounded follow-up confirms first authorization reloads the current actor-owned signing wallet, chain/address/custody and consent before authorization CAS. Discovery now applies actor and tenant predicates before snapshot ordering and pagination. These two findings are source-closed. Full provider preparation recovery, pre-sign cumulative budgets and protocol fixtures remain pending author handoff and subsequent verification.

Coordinator inspected the Ethereum native slice (exact create/call artifact descriptors, quote and pre-nonce gas/fee/value authorization, allocator-backed immutable preparation, exact-wire recovery and finalized runtime-code proof), plus its authored lost-preparation/lost-send and tamper fixtures. No additional blocker identified in that bounded read. This is not full provider/consumer approval: real local-chain adapter execution, the other native providers, action quote admission and complete client journeys remain unverified or in progress.

Coordinator independently reread the action quote admission slice: StartActionAsync, QuoteCore/RestoreActionQuote, DeploymentActionResolver and SurrealDeploymentStore.Fenced. The winning quote is persisted under deterministic operation identity; normalization is limited to operation key, protocol identity and quote fields. Stored action run/node/hash participate in atomic admission and initial authorization. No additional blocker in that bounded source read. Requested explicit malformed saved native-intent handling and real-store canceled-admission/authorization plus authorized-observation regression. No runtime checks performed.

AVM staged-source review covered native quote, artifact validation, preparation journal/store and observation. The pre-sign plan and exact signed candidate are persisted and genesis/fee/rent checks precede preparation. Required correction: a NoOp call's program can be updated between the pre-sign application lookup and execution; confirmation cannot label the selected compiled-program hash as executed without code-version evidence at the call position (or an authoritatively verified immutable-app contract). Add update-between-preparation-and-execution coverage. Indexed absence must reject pagination/incomplete history before ExpiredNoEffect. Author notified; this is an artifact-semantics requirement, not a product deferral.

## Runtime authority and composition follow-up

Independent lane A review confirmed that root/nested bundle visibility and bound Holon authority are checked before the action admission transaction but not carried into it. Carry server-derived witnesses and atomically validate the relevant rows with run/outbox creation. Exact invocation replay must recover an already-admitted actor/tenant/intent receipt before applying new-admission live checks, otherwise a lost response followed by revocation hides the original run. Immutable root deployed versions may intentionally survive later artifact generation; that policy is distinct from current caller authority.

Compiler fixes now preserve parallel leaves, reject competing field mappings, and rewrite gate/edge predicates while preserving quoted literals. A further lexer correction was requested for hyphenated path segments and nested fields named run/upstream. Source implementation and regression authoring continue; no runtime acceptance.

## Consumer and cancellation reread

Root independently read the deployment console, operation discovery/continuation/invocation UI and SDK regressions. Original start requests and keys survive failed responses; account changes unmount the editor; quote limits and original authority remain visible; terminal run evidence gates a fresh invocation. The general frontend SDK calls the runtime-configured backend directly, so no operator BFF allowlist expansion is needed. Source review found cancellation ignored a mismatching incumbent intent; the author now compares canonical request bytes and intent hash before cancellation or replay, including start-against-tombstone. Root reread accepted that correction. Native partial-resource cleanup remains explicitly in implementation; terminal failure must not imply resources were unwound. Tests/typechecking remain unrun.
