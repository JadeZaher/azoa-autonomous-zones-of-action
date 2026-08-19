---
type: plan
track: ardanova-financial-workflow-conformance
created: 2026-07-18
status: in_progress
---

# Plan: ArdaNova financial workflow conformance

1. [ ] Reconcile the self-sovereign ArdaNova contract with the generic
   tenant-custodial onboarding baseline and record the selected launch path.
2. [ ] Publish the versioned endpoint/auth/idempotency/state/secret matrix.
3. [ ] Add operator-issued fixed-scope key and real API-key middleware tests.
4. [ ] Add live-Surreal deterministic ensure, replay, divergent-key,
   concurrency, and cross-tenant isolation tests for the selected account path.
5. [ ] Add capability, wallet, KYC, and no-secret projection tests.
6. [ ] Add fail-closed allocation/reward and reconcile-before-retry tests with
   simulated providers only.
7. [ ] Run the SDK against the live in-process endpoints and verify the versioned
   contract shapes without mocked transport.
8. [ ] Run the integrated local and CI gates, record evidence, independently
   review the boundary, and archive only when every criterion is proven.
