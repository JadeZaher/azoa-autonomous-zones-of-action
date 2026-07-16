---
type: plan
track: node-public-governance-transparency
created: 2026-07-11
status: in_progress
---

# Plan: public node-governance transparency

1. [x] Add separate public contracts, read-only store/manager, and anonymous
   controller for current policy and three audit histories.
2. [x] Add typed snapshot parsing, strict redaction, bounded composite cursors,
   semantic ETags, generic failures, and debug-detail suppression.
3. [x] Require a persistent/versioned Data Protection key ring outside local
   development and mount it in the reference compose deployment.
4. [x] Add unit coverage for tamper, cross-purpose/cross-instance cursors,
   redaction, weak/any ETags, error caching, and controller behavior, plus a live
   equal-timestamp keyset test.
5. [x] Replace trust-all forwarded headers with configured trusted
   proxies/networks or a fail-fast edge-only deployment mode and test the rate
   limiter partition source.
6. [x] Add an opt-in signed, privacy-safe audit checkpoint with a separately
   protected node-identity key, deterministic offline verifier, exact-prefix
   rewrite detection, and bounded fail-closed reads. This is not an external
   witness, a DB-root remediation, federation, or an egress-fee activation.
7. [ ] Run the integrated build/unit/live-Surreal sweep, record evidence, then
   archive this track. Signed externally checkpointed history remains related
   conformance/federation work, not a false blocker hidden inside this slice.
