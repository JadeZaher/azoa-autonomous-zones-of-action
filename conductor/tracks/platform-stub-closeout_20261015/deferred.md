---
type: deferred-work
---

# Unavoidable deferrals

Historical credential and provider-approval claims are being reverified; they are not accepted as automatic deferrals.

Any entry must state the affected capability, evidence of the external blocker, work already completed, owner, exact unblock condition, and verification needed for closure. Implementation difficulty alone does not justify deferral.

## IDENTOMAT-WORKFLOWS: vendor dashboard configuration

- Capability: real Identomat KYC and KYB hosted verification.
- Evidence: the owner confirmed the workflows do not exist yet and explicitly authorized mocked verification without them. A company API key exists locally, but no configuration IDs were supplied. No configuration ID has been fabricated.
- Work implemented: real HTTP adapters, shared authenticated callback routing, tenant-owned KYB attempts, lease-fenced outcomes, and fixture-based regression coverage. Execution of the integrated verification sweep is still pending.
- Owner: platform operator with access to the Identomat dashboard.
- Unblock: create the KYC and KYB workflows, provide their configuration IDs, and register the shared company callback URL and callback key. Local development uses the ignored `.env`; deployed settings must be configured separately.
- Closure evidence: run both hosted workflows against Identomat, verify authoritative result retrieval and callback replay, and demonstrate that incomplete or rejected evidence cannot open the relevant gates. Mock coverage alone does not close this entry.

## IDENTOMAT-RECOVERY: ambiguous create reconciliation

- Capability: automatic recovery after `/begin` may have succeeded but its response
  or local attachment was lost.
- External evidence: the official [API reference](https://docs.identomat.com/developer-tools/api-reference.md)
  documents `companyKey`, `configId`, `clientUserId` and optional parent `sessionId`,
  but no idempotency key. `clientUserId` is described as applicant correlation;
  supplying an existing parent `sessionId` explicitly creates a new child.
  Captures: `.omc/research/identomat-api-2026-10-15.md` and
  `.omc/research/identomat-recovery-callbacks-2026-10-15.md` (2026-10-15).
- Completed: only the durable admission creator calls the provider; retries resume
  attached sessions. Ambiguous unattached claims fail closed with reconciliation
  status. Provably unsent local-budget failures safely release their own admission.
  Audited operator endpoints list NeedsOperator admissions and attach a
  provider-verified session after subject review. An absence decision releases
  only a locally proven unsent admission. Once sending has begun, an inconclusive
  absence decision quarantines the admission and retains its key. A late original
  response may attach under the original creator token. Authorized reverification
  uses durable per-attempt admission; it is implemented rather than deferred.
- Owner: platform operator with Identomat integration support.
- Unblock: obtain documented idempotent creation or an authoritative lookup by
  unique client attempt that can distinguish absent from pending/created sessions.
  Until then, an operator must locate and attach the real session; human absence
  attestation alone cannot clear a possibly-sent claim or authorize another send.
- Closure evidence: vendor-contract fixtures and a live sandbox lost-response drill
  proving that concurrent and crash-recovery retries create one vendor session,
  proving automated reconciliation when the vendor adds the needed contract.
  The operator reconciliation path is implemented and is not deferred.
