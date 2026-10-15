---
type: track-spec
---

# Platform stub closeout

Close existing production stubs and incomplete integration flows, prioritizing Identomat KYC/KYB and then Stripe. Reconcile existing implementation before writing replacements. Consolidate local and remote branches into main and clean up integrated Git branches/worktrees first, preserving unique work.

## Acceptance

- Every discovered production stub has implementation and verification evidence, or an unavoidable external dependency recorded in `deferred.md` with a concrete unblock condition.
- Identomat KYC and KYB follow current vendor contracts, with durable status handling, authorization, replay protection, and SDK/frontend parity where exposed.
- Stripe payment and Connect flows use real provider contracts and preserve financial idempotency and authoritative webhook handling.
- Main contains all outstanding branch work; redundant branches and worktrees are removed only after preservation is established.
- Independent review and an integrated final test/lint/typecheck sweep provide evidence. No simulated success is accepted as live-provider verification.

Historical tracks are evidence to reconcile, not proof that work is absent or complete. This track does not automatically expand implementation into sibling consumer repositories.
