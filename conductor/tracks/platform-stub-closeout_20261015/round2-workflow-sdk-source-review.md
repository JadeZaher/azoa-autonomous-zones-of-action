---
type: code-review
status: accepted-bounded-source
---

# Ordinary workflow SDK source review

Independent reviewer: Lane B. Author: root. Runtime: NOT RUN.

Scope: sdk/azoa-wallet/src/workflow/{types,client,run,start-options,index}.ts,
root exports, API executeAction options, and workflow/run-driver.test.ts.

The required key/header and consent/publication-hash query match the authored backend.
Actor-scoped start/status use the child credential, and actor selection locks before queued
operations. Start options are copied synchronously so later caller mutation cannot replace
queued key or consent. Existing run state stays asynchronous and observable.

P1 found and mitigated: fluent template start previously instantiated a fresh action for
every retry, defeating action-scoped workflow idempotency and losing child authority at the
instantiation call. The current template start rejects before instantiating. Explicit
instantiate, persist action ID, preview and keyed action start remains usable; source fixture
covers that journey. This mitigation is not full advertised convenience completion.

REQUIRED ROUND 3 implementation: durable keyed template instantiation binds template
revision, canonical substitutions, actor/tenant and key, copies stable graph IDs atomically,
and returns the exact original action on replay before mutable source checks. Define
parent-authoring versus child-running authority explicitly; then restore the safe fluent
convenience and test lost instantiate/start responses and account changes.

No further blocking defect found in the bounded ordinary start/status/key source after
correction. This verdict does not approve deferred template behavior as complete and does
not substitute for the final integrated runtime sweep.
