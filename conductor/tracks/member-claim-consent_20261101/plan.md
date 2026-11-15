---
type: plan
status: implementation-verified-release-gated
---

# Member claim and consent

ArdaNova takeover N3/F implements the accepted P5-16/P5-20 plan: private immutable
descriptor, protected operator delivery stub, atomically replaced invitations, hosted
credential/consent/bank flow, consent-only renewal and SDK first-party dogfood.

Implementation and independent source review are complete. The tested source includes
claim/consent renewal, exact signed login-cut checks on generic consent writes, immutable
descriptors, atomic invite replacement, protected operator-only delivery, first-party bank
setup, SDK money screens and permanent cash-out void-or-reveal fencing. A void never aborts
an admitted payout. Missing versus present auth watermarks are fenced explicitly; Surreal
`NONE` is distinct from bound JSON null.

## Verification evidence

Validation used only isolated SurrealDB at `http://127.0.0.1:8127`, container
`codex-payment-surreal-c17ff8f2`, plus fresh loopback Ethereum fixtures with ephemeral keys.
No production data or demo SurrealDB was used. Golden generation was unset throughout
verification; the earlier 148-case generation pass produced the two new table goldens.

- Unit sweep: 5,670 executed results, initially 5,669 passed and one architecture failure.
  The repaired architecture class subsequently passed all 50 cases; no unit failure remains.
- Normal CI integration filter: `Category!=Chaos&Category!=Perf&Category!=LocalChain&Gate!=WttSixRoute`.
  The initial full run completed in 61m08.160s with 1,188 passed, 13 failed and one known
  analyzer-only G3 skip. All failures were collected before the coordinated repair.
- Bounded repair runs covered the affected classes and new rollback proofs. Merging the
  latest result for each integration case gives **1,205 passed, zero failed and one known
  G3 skip across 1,206 unique cases**. This is cumulative full-run plus targeted evidence,
  not a claim that a second full integration sweep ran.
- All five handoff HTTP cases passed, including tokenless claimed-member renewal with a
  non-null cut, exact approval terms, invite replacement, expired approval resolution,
  approval/decline racing and the tenant's typed `REQUEST_KEY_IN_USE` boundary after KYB.
- All six generic consent login-cut HTTP cases passed: missing, malformed, duplicate,
  older and same-second stale cuts are refused; actual fresh issuer tokens and legacy
  unwatermarked accounts remain supported.
- All four real cash-out persistence cases passed, including sixteen paired claim-versus-void
  races, permanent tombstone replay, different-driver refusal and unchanged legacy-row reveal.
- New real-database guards proved stale/disabled-member rollback, losing-resend retirement
  rollback and actual `PAYOUT_ALREADY_ADMITTED` rollback before revealing a legacy payout.
  Both store-family inventory checks passed with real proof references and no gaps added.
- Pinned Ethereum contracts built and passed 12/12 tests with no skips. All three .NET
  Ethereum protocol cases subsequently passed using those local prerequisites.
- Node frontend lint, typecheck and production build passed; 326/326 unit/slice tests passed.

Local evidence is intentionally uncommitted: node `.omc/research/payment-node-validation-final-2026-11-01.json`,
`.omc/research/payment-node-source-attestation-2026-11-01.json`, the referenced TRXs under
`.omc/research/payment-node-final-results/`, and contract test logs under `.omc/research/`.
The takeover host holds frontend repair logs under its own `.omc/research/`.

## Release gates

- `Tenant:ClaimInvites:Enabled` and `Fiat:OffRamp:RequestVoidEnabled` remain **false**.
  Production stub delivery additionally requires explicit `StubDeliveryAcknowledged`.
  No real email delivery was introduced.
- Existing DataProtection key-ring backup/persistence remains an operator release gate.
  Automatic approval rejected the protected-key backup; explicit owner authorization is
  required before that action. No protected key material was copied by this work.
- SDK source version 0.6.0 remains unpublished. Its immutable package publication and
  consuming-app adoption must finish before enabling the new integration in production.
- Enable cash-out void only after all older admission processes are drained and bounded
  in-flight database work has cleared. The new admission fence is unconditional; the new
  void endpoint stays closed until the explicit rollout switch is enabled.
- Review-branch CI and the reviewed release/migration sequence precede a main-branch merge
  or production deployment. No production payment deployment is claimed by this track.
- Live Identomat configuration remains an external owner prerequisite.

Residual P5-21: recipient address is asserted by the tenant; a compromised tenant
can redirect its member's invite. Identity re-check tied to KYC person key remains
a separate policy change. Live Identomat remains an external prerequisite.
