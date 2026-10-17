---
type: review
---

# Round 2 governance independent source review

Reviewer: Lane A executor, separate from governance author. Scope: ConsumerGovernanceService and finalization partial, GovernanceActionResolver, GovernanceCommandIdentity and persistence models, three handlers, SurrealGovernanceStore partials, shared action/policy/evidence fences, reserved claim namespace, Holon projection protection, governance unit tests and SurrealGovernanceTests. No build or test execution occurred.

## Required correction

**P1 ? multi-statement raw query construction.** CreateAsync, Admin, VoteAsync (including cast guard), and FinalizeAsync pass semicolon-separated statements to one `SurrealQuery.Of`. The pinned library requires a single statement per `Of`; transaction composition must be flat through `Combine`. `NodeGovernancePolicyFence.Create` has the same issue. Split the literal statements and retain one shared parameter binding per distinct parameter name. Review nested IF bodies carefully rather than blindly splitting arbitrary SQL. The source verdict remains pending correction and reread; final integrated database execution must verify all paths.

## Bounded conclusions

Command identity binds immutable frozen input, actor, tenant, run and node. Successful and failed receipts share the same atomic identity; replay returns committed evidence without applying an old vote again. The failed-receipt path participates in ActionExecutionFence and refuses a canceled command with no prior receipt. Valid frozen JSON is preserved by the recovery envelope; malformed typed configuration is a deterministic failure, while mutable authority conflicts remain recoverable.

The vote transaction joins the subject evidence fence, current active membership, exact approval/selection/trust revision, identity binding, matching AML lineage and database cutoff. Generic person-claim acquisition and release reject the election namespace. Election definition and snapshot membership are authoritative; public Holons are guarded projections. Finalization locks the proposal, checks database cutoff and counts, and publishes one immutable result; later offboarding does not remove cast-time evidence.

No additional concrete security or semantic blocker was identified in this bounded source pass. This is not runtime approval. Real Surreal tests cover authority changes, cancellation, command replay versus revote, duplicate-person rejection, policy revision, concurrent AML append versus vote, concurrent finalization, and post-cutoff publication. The concurrent evidence fixture permits either legitimate serial order; it does not force an instrumented interleaving.

## Pruning

Several touched HolonManager comments still describe a freeze only after the first vote and refer to removed `#proposal-freeze` detail; the implementation now rejects all generic projection edits. The proposal config also retains a verbose, mojibake-bearing scope comment. Shorten these to current behavior and place rationale in directory documentation.

## Correction handoff

At the coordinator's request, the reviewer subsequently authored the narrow query-construction correction. Create/Admin/Vote/Finalize and NodeGovernancePolicyFence now expose flat arrays of individual statements; parameters remain bound on the last statement of each group and merge into the single outer transaction. Optional parent locking uses one conditional expression, avoiding an empty-record lookup when no parent exists. Obsolete first-vote-only projection comments and the verbose scope comment were pruned. These corrections require the coordinator's separate approval; the reviewer does not self-approve this authored follow-up. Existing real persistence tests remain the final runtime gate.

Coordinator independent reread accepted the query-composition correction and hosted callback acknowledgement correction by source only. No additional blocker was found in that narrow reread. Runtime verification remains pending the final integrated sweep.
