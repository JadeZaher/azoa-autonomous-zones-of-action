---
type: spec
track: azoa-code-style-backpropagation
created: 2026-07-11
status: in_progress
horizon: alpha-hardening
---

# Track: AZOA code-style back-propagation

## Goal

Make repository style mechanically converge on typed, reusable primitives rather
than asking reviewers to remember preferences. The reproducible 2026-07-11
pre-migration production census found 78 mutation-bearing raw literals (17
`CREATE`, 15 `UPSERT`, 32 `UPDATE`, 3 `UPDATE ONLY`, 11 `DELETE`), three dynamic
conditional updates in `SurrealBridgeStore`, and 219 production catch-all blocks.
Only seven literal fragments are inside explicit `BEGIN`/`COMMIT` transactions;
the other 71 are standalone debt. The checked-in per-file ratchet is a ceiling,
not an acceptable steady-state target.

Every inventory row has one of three dispositions:

1. justified multi-table/multi-statement atomic transaction;
2. ordinary mutation to migrate to a typed primitive;
3. workflow that must first gain transaction atomicity, then use typed components
   where composition permits it.

The third bucket caught real crash windows. Saga compensation and forward
continuation now use lease-guarded `BEGIN`/`COMMIT` requests, so neither can
persist its source transition without its successor. Quest aggregate upsert,
Quest aggregate delete, and API-key create-plus-scope-write remain atomicity
remediation items; a mechanical typed rewrite must not bless their split writes.

## Required standards

1. Ordinary reads use typed LINQ/query primitives; ordinary create/upsert uses
   `SurrealWriter`; single-record conditional mutation uses a typed builder.
2. DDL stays in generated schema tooling and graph edges use the typed relation
   builder. Raw SurrealQL is limited to genuinely multi-table/multi-statement
   atomic transactions. A temporarily unsupported single statement needs a
   linked, expiring SurrealForge issue/track waiver. Each escape hatch names its
   atomic invariant or waiver in one line and in its directory guide.
3. Expected domain outcomes use typed results. Unexpected exceptions reach one
   centralized HTTP/worker observability boundary with their original stack.
4. Interfaces own XML contracts; implementations inherit them. Reusable helpers
   live in the generic package or the owning domain helper directory.
5. A changed-file pruning gate checks raw SQL, catch-all swallowing, duplicated
   helpers, copied docs, stale comments, and unused imports on every turn.
6. Generic SurrealForge source, examples, tests, and package documentation stay
   domain-neutral; AZOA-specific rationale remains in this repository.

## Active expiring waiver

`Providers/Stores/Surreal/SurrealNodeTransparencyStore.cs` uses two raw SELECT
variants for one descending `(occurred_at,id)` keyset pagination path. Owner:
`SurrealForge.Client` typed query surface. This track is the reviewed
package-remediation owner; the waiver
expires 2026-09-30. The current builder cannot express `time < cursor OR
(time == cursor AND id < record(cursor-id))`.

## Acceptance criteria

- SurrealForge exposes typed create/upsert plus multi-predicate, multi-assignment
  conditional update/delete primitives with identifier validation, immutable
  composition, parameter collision safety, unit tests, and clean-consumer proof.
- AZOA consumes the cross-repository work only after SurrealForge has clean tests
  for every target framework, a deterministic package artifact, a recorded
  version/tag, and either published-feed evidence or a checked clean-consumer
  local-feed restore. It does not fork query machinery into an app helper.
- The Holon reservation/finalize/release path contains no raw CRUD statement and
  retains real-SurrealDB single-winner/idempotency coverage.
- An architecture debt-budget gate prevents raw mutation and catch-all counts
  from increasing; each migration wave lowers the checked-in ceiling.
- Store batches are migrated role-by-role until ordinary raw CRUD and store-level
  catch-all swallowing reach zero. Legitimate transaction escape hatches remain
  explicitly documented and tested.
- Touched interfaces have complete parameter/result semantics, and their
  implementations use `inheritdoc` rather than duplicated prose.
- Logging has one minimum-level knob: medium is the neutral default, Development
  overrides to full diagnostic output, and Production overrides to critical-only
  capture. Every unexpected exception is recorded once.
- Split-write workflows in the atomicity-remediation bucket are made crash-safe;
  lowering a raw count without closing their partial-write window does not pass.

## Non-goals

- Replacing atomic multi-table transactions with several typed requests.
- Hiding expected concurrency conflicts inside global exception middleware.
- Bulk mechanical rewrites without behavior-level tests.
