---
type: plan
track: azoa-code-style-backpropagation
created: 2026-07-11
status: in_progress
---

# Plan: AZOA code-style back-propagation

1. Extend SurrealForge's generic conditional mutation builder to support several
   predicates and assignments, record-link/string coercion, conditional delete,
   and affected-row assertions.
2. Test and pack the package independently; update AZOA through a reproducible
   package reference rather than copying query code into the application. Record
   target-framework test evidence, deterministic pack output, version/tag, and a
   clean-consumer restore before changing AZOA's package reference.
3. Convert the NFT reservation/finalization/release slice and add inherited
   interface documentation plus shared result/helper usage.
4. Add debt-budget architecture tests for raw mutations and store catch-all
   blocks. Check in an exact classified inventory (file, operation, invariant or
   expiring waiver), record the post-conversion baseline, and require it never to
   rise.
   The current ratchet lowers production catch-alls from 219 to 205 by pruning
   blanket wrappers in the touched Holon/governance stores and consolidating
   idempotency recovery; raw mutation literals are 77;
   further waves only lower it. Typed key reads also lower the Holon store's
   raw-query ceiling 6 → 4.
5. Migrate remaining stores in independent batches: identity, quest/workflow,
   governance, value/bridge, then residual utilities. Keep multi-table atomic
   transactions raw until the package can model them without extra round trips.
   Before mechanical migration, close the known split-write windows in Quest
   aggregate upsert/delete and API-key create-plus-scope update. Saga
   compensation and forward continuation are lease-guarded transactions with
   rollback, stale-worker, and single-successor coverage.
6. Consolidate exception logging so each unexpected exception is recorded once,
   with development verbosity and production severity controlled by config.
7. Archive only after the ceilings reach their justified escape-hatch inventory,
   all changed-file pruning gates pass, and a secondary review approves.

## Secondary-review closure (2026-07-12)

The independent package review required two safeguards before the 0.4.0 typed
mutation package can be published: every member used by a typed mutation
predicate must be in the same persisted-field plan as typed assignments, and
typed `RecordId<T>` overloads must preserve opaque ids without string-prefix
parsing. Both are covered by client tests. The package version is now one
coordinated 0.4.0 publish set so Schema cannot be packed as 0.3.0 while
depending on Client 0.4.0. AZOA continues to use its existing package until a
clean consumer restore and explicit publish decision are recorded.

## Package-consumer handoff (2026-07-12)

`SurrealId.BareRecordId`, `ParseRecordGuid`, and `ParseOptionalRecordGuid` are
implemented and tested in the local SurrealForge 0.4.0 source, but AZOA consumes
NuGet `SurrealForge.Client` 0.2.0 and the local source has no clean release tag.
Do not copy those conversions into AZOA as a permanent helper. The package owner
must validate a clean coordinated 0.4.0 build/pack, create the trusted-publishing
release, and record the immutable version/tag and clean AZOA consumer restore.
Only then may AZOA upgrade all coordinated SurrealForge references, replace any
temporary response-boundary conversion with `SurrealId`, and delete the helper.
