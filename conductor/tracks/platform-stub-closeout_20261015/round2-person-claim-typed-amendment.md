---
type: design
status: accepted source; runtime verification pending
---

# Typed person-operation claim mutation

Replace the expiring conditional-writer waiver without moving claim authority into a preceding read. Public store signatures and incumbent-versus-claimant result semantics remain unchanged. Governance retains its dedicated command path and generic APIs continue rejecting its reserved namespace.

## Persistence and atomic admission

Add optional integer Version to PersonOperationClaim. New generic claims begin at 1; a legacy absent Version is admitted only through an explicit NONE predicate and advances to 1. Ordinary reads use typed deterministic Key. Existing-row mutations use immutable typed UpdateOnly builders and Set/Unset; new rows use typed Create. Generic mutations preserve governance fields, immutable identity, CreatedDate and the first decision cutoff.

An application snapshot constructs the candidate mutation, but never authorizes it. One flat transaction contains BEGIN, raw atomic guard statements, the typed mutation (if eligible), and COMMIT. The guard selects the deterministic record inside the transaction, checks exact expected existence/version/holder/released state and immutable scope/operation/person identity, and evaluates the cutoff using time::now(). The typed conditional writer repeats expected version/holder/release conditions. A mismatch or zero-row CAS causes bounded reread/reconstruction; a unique first-create collision does the same. Only recognized uniqueness, guard conflict and transient transaction conflicts retry. Unexpected storage errors propagate. Exhaustion returns a typed retryable conflict, never success.

The guard is justified raw multi-statement atomic coordination: it couples database-time predicates and snapshot expectations with a typed mutation. It performs no handwritten ordinary CRUD. No nested Combine and no semicolon batch passed to Of. Bound parameters are scoped where typed writers coexist. A missing create must also pass database-time validation of a requested decision cutoff before Create.

## Preserved behavior

A live incumbent belonging to another claimant is returned unchanged, including decision and tenant. Same-holder retries preserve ClaimedAt and may update a decision only before the pinned cutoff. An unheld or released claim can be acquired with new holder/tenant/claim time; release markers clear atomically. No-decision acquisition preserves existing decision fields. The first actual decision fixes its cutoff (including the existing never-closes sentinel); subsequent candidates cannot extend or replace it. Once the cutoff passes, claim mutation fails and release returns false. Release succeeds only for the current live holder and remains idempotent. Storage timestamp normalization and external sentinel-to-null projection remain compatible.

The transaction returns a committed row from the mutation response, or a typed in-transaction Key read for an unchanged incumbent. Application clocks can supply descriptive timestamps, but cannot override database cutoff admission. Optional values use Unset and literal NONE predicates, not captured null bindings. Literal NULL or inconsistent legacy state fails closed rather than being silently assigned ownership.

## Authored verification before final sweep

Real Surreal fixtures: concurrent missing-row contenders; same-holder replay versus another claimant; release/reclaim races; stale version CAS; same-holder decision update; immutable cutoff under attempted extension; absent-version adoption; NONE tenant/release/decision handling; expired first decision; database cutoff for claim/release despite old application timestamp; incumbent receives no candidate tenant/decision; no-decision reacquisition compatibility; reserved governance namespace refusal. Existing caller/compensation tests retain their assertions. Remove the waiver only after source review of the replacement and keep runtime status pending the integrated sweep.

## Design acceptance and source handoff

Coordinator adversarial review accepted this design with explicit requirements: literal NULL is never adopted as NONE, Version survives storage mapping, cutoff/reclaim behavior is covered, typed CAS repeats guarded authority, unchanged incumbent reads are coherent, and zero-CAS responses cannot succeed. The implementation follows that contract. Eleven real persistence/fault-seam fixtures are authored; no executions occurred. The expired single-statement escape hatch is removed from store code. Independent source review and the final integrated runtime sweep remain required.

Coordinator independent source reread accepted the bounded implementation. Defensive empty scope/operation/person validation was added to claim, lookup and release with an authored fixture. The prior waiver is retired as a source replacement. Runtime verification remains mandatory at the final sweep.
